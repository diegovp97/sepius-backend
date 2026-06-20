using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.API.Workers;

/// <summary>
/// Worker que monitoriza periódicamente los canales de Twitch.
///
/// CONCEPTOS CLAVE DE BackgroundService:
///
/// 1. CICLO DE VIDA: El host de ASP.NET Core inicia este servicio automáticamente
///    al arrancar y lo detiene (con CancellationToken) cuando la app se apaga.
///    En Node.js harías esto con setInterval() + process.on('SIGTERM', cleanup).
///
/// 2. EL PROBLEMA DEL SCOPE: BackgroundService corre en un hilo propio y vive
///    durante toda la app (Singleton implícito). Si inyectas un servicio Scoped
///    (como un DbContext de EF Core) directamente en el constructor, obtendrás
///    una excepción en tiempo de arranque porque los servicios Scoped no pueden
///    ser consumidos por Singletons directamente.
///    SOLUCIÓN: Inyectar IServiceScopeFactory y crear scopes manualmente.
///    En NestJS es equivalente a usar moduleRef.get() dentro de un scheduler.
///
/// 3. PeriodicTimer vs Task.Delay:
///    - Task.Delay(X) espera X ms desde que termina la iteración anterior.
///    - PeriodicTimer dispara cada X ms en tiempo de reloj real (más preciso).
/// </summary>
public sealed class TwitchMonitorWorker : BackgroundService
{
    private readonly ILogger<TwitchMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitorOptions _options;

    public TwitchMonitorWorker(
        ILogger<TwitchMonitorWorker> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<MonitorOptions> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TwitchMonitorWorker iniciado. Intervalo: {Interval}s",
            _options.PollingIntervalSeconds);

        // Suscribirse al evento de grabación completada para subir a YouTube
        using var scope = _scopeFactory.CreateAsyncScope();
        var streamlink = scope.ServiceProvider.GetRequiredService<IStreamlinkService>();
        var youtubeUpload = scope.ServiceProvider.GetRequiredService<IYouTubeUploadService>();

        streamlink.RecordingCompleted += async (recording) =>
        {
            _logger.LogInformation(
                "Grabación completada para '{Channel}'. Iniciando subida a YouTube...",
                recording.ChannelName);
            await youtubeUpload.UploadAsync(recording);
        };

        // PeriodicTimer es la forma moderna (.NET 6+) de hacer polling periódico.
        // Se cancela limpiamente cuando stoppingToken es cancelado.
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.PollingIntervalSeconds));

        // WaitForNextTickAsync devuelve false cuando el timer es dispose/cancelado
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckChannelsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown solicitado durante el tick, salir limpiamente
                break;
            }
            catch (Exception ex)
            {
                // IMPORTANTE: NO propagar la excepción.
                // Si ExecuteAsync lanza, el GenericHost detiene toda la app.
                // El worker debe ser resiliente a errores transitorios (ej: timeout de red).
                _logger.LogError(ex, "Error durante el ciclo de verificación de canales.");
            }
        }

        _logger.LogInformation("TwitchMonitorWorker detenido.");
    }

    private async Task CheckChannelsAsync(CancellationToken ct)
    {
        // Crear un scope de DI para poder resolver servicios Scoped o Singleton
        // desde dentro del worker Singleton.
        // using var: el scope (y sus servicios) se libera al salir del bloque.
        using var scope = _scopeFactory.CreateAsyncScope();
        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var twitchApi = scope.ServiceProvider.GetRequiredService<ITwitchApiService>();
        var streamlink = scope.ServiceProvider.GetRequiredService<IStreamlinkService>();

        var channels = await channelRepo.GetAllAsync(ct);
        var monitored = channels.Where(c => c.IsMonitored).ToList();

        if (monitored.Count == 0)
        {
            _logger.LogDebug("No hay canales monitorizados configurados.");
            return;
        }

        _logger.LogDebug("Verificando {Count} canal(es)...", monitored.Count);

        foreach (var channel in monitored)
        {
            // Respetar la cancelación entre canales (útil si hay muchos)
            ct.ThrowIfCancellationRequested();

            try
            {
                var isLive = await twitchApi.IsChannelLiveAsync(channel.Name, ct);

                if (isLive && !streamlink.IsRecording(channel.Name))
                {
                    _logger.LogInformation("'{Channel}' está en DIRECTO. Iniciando grabación.", channel.Name);
                    await streamlink.StartRecordingAsync(channel.Name, ct);
                }
                else if (!isLive && streamlink.IsRecording(channel.Name))
                {
                    _logger.LogInformation("'{Channel}' ha terminado el directo. Deteniendo grabación.", channel.Name);
                    await streamlink.StopRecordingAsync(channel.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Error en un canal específico no debe detener la verificación del resto
                _logger.LogError(ex, "Error verificando canal '{Channel}'", channel.Name);
            }
        }
    }
}
