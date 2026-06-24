using System.Text.Json;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;

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
/// <summary>
/// Worker de polling exclusivo para plataformas no-Twitch (actualmente Kick).
/// Twitch se gestiona en tiempo real mediante TwitchEventSubWorker.
/// </summary>
public sealed class TwitchMonitorWorker : BackgroundService
{
    private readonly ILogger<TwitchMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitorOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public TwitchMonitorWorker(
        ILogger<TwitchMonitorWorker> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<MonitorOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TwitchMonitorWorker iniciado. Intervalo: {Interval}s",
            _options.PollingIntervalSeconds);

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
        using var scope = _scopeFactory.CreateAsyncScope();
        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var streamlink  = scope.ServiceProvider.GetRequiredService<IStreamlinkService>();

        var channels = await channelRepo.GetAllAsync(ct);

        // Este worker solo gestiona canales Kick; Twitch lo maneja TwitchEventSubWorker
        var kickChannels = channels
            .Where(c => c.IsMonitored && c.Name.StartsWith("kick:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (kickChannels.Count == 0)
        {
            _logger.LogDebug("No hay canales Kick monitorizados.");
            return;
        }

        _logger.LogDebug("Verificando {Count} canal(es) Kick...", kickChannels.Count);

        using var httpClient = _httpClientFactory.CreateClient("Kick");

        foreach (var channel in kickChannels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var slug  = channel.Name["kick:".Length..];
                // null = estado desconocido (ej: 403 Cloudflare) → no cambiar nada
                var isLive = await IsKickChannelLiveAsync(slug, httpClient, ct);

                if (isLive == true && !streamlink.IsRecording(channel.Name))
                {
                    _logger.LogInformation("'{Channel}' está en DIRECTO (Kick). Iniciando grabación.", channel.Name);
                    await streamlink.StartRecordingAsync(channel.Name, ct);
                }
                else if (isLive == false && streamlink.IsRecording(channel.Name))
                {
                    _logger.LogInformation("'{Channel}' ha terminado el directo (Kick). Deteniendo grabación.", channel.Name);
                    await streamlink.StopRecordingAsync(channel.Name);
                }
                else if (isLive == null)
                {
                    _logger.LogDebug("Estado de '{Channel}' desconocido (posible bloqueo Cloudflare). Estado actual mantenido.", channel.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error verificando canal Kick '{Channel}'", channel.Name);
            }
        }
    }

    /// <summary>
    /// Comprueba si un canal de Kick está en directo usando la API pública de Kick.
    /// Devuelve: true=live, false=offline, null=estado desconocido (403/error de red).
    /// </summary>
    private async Task<bool?> IsKickChannelLiveAsync(string slug, HttpClient httpClient, CancellationToken ct)
    {
        try
        {
            var url = $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(slug)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Cloudflare bloqueó la petición — no sabemos el estado, mantener el actual
                _logger.LogWarning("Kick API devolvió {Status} para '{Slug}' (posible Cloudflare). Estado mantenido.",
                    response.StatusCode, slug);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Kick API devolvió {Status} para '{Slug}'", response.StatusCode, slug);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc  = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // "livestream" es null cuando offline, objeto cuando en directo
            return doc.RootElement.TryGetProperty("livestream", out var ls)
                   && ls.ValueKind != JsonValueKind.Null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error al consultar Kick API para '{Slug}'", slug);
            return null;
        }
    }
}
