using System.Diagnostics;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.Streamlink;

namespace Sepius.Recorder.Workers;

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
    private readonly StreamlinkOptions _streamlinkOptions;

    public TwitchMonitorWorker(
        ILogger<TwitchMonitorWorker> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<MonitorOptions> options,
        IOptions<StreamlinkOptions> streamlinkOptions)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _streamlinkOptions = streamlinkOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TwitchMonitorWorker iniciado. Intervalo: {Interval}s",
            _options.PollingIntervalSeconds);

        // RecordingCompleted ahora se gestiona en LiveTranscodeService (Pipeline unificado)

        // PeriodicTimer es la forma moderna (.NET 6+) de hacer polling periódico.
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
        var channelRepo   = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var liveTranscode = scope.ServiceProvider.GetRequiredService<ILiveTranscodeService>();

        var channels = await channelRepo.GetAllAsync(ct);

        // Este worker solo gestiona canales Kick; Twitch lo maneja TwitchEventSubWorker
        var kickChannels = channels
            .Where(c => c.IsMonitored && c.Name.StartsWith("kick:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var starviosChannels = channels
            .Where(c => c.IsMonitored && c.Name.StartsWith("starvios:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (starviosChannels.Count > 0)
            await CheckStarviosAsync(starviosChannels, liveTranscode, ct);

        if (kickChannels.Count == 0)
        {
            _logger.LogDebug("No hay canales Kick monitorizados.");
            return;
        }

        _logger.LogDebug("Verificando {Count} canal(es) Kick...", kickChannels.Count);

        foreach (var channel in kickChannels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var slug  = channel.Name["kick:".Length..];
                // null = estado desconocido (streamlink no pudo conectar) → no cambiar nada
                var isLive = await IsKickChannelLiveAsync(slug, ct);

                if (isLive == true && !liveTranscode.IsTranscoding(slug, "kick"))
                {
                    _logger.LogInformation("'{Channel}' está en DIRECTO (Kick). Iniciando HLS + grabación.", channel.Name);
                    await liveTranscode.StartAsync(slug, "kick", ct);
                }
                else if (isLive == false && liveTranscode.IsTranscoding(slug, "kick"))
                {
                    _logger.LogInformation("'{Channel}' ha terminado el directo (Kick). Deteniendo HLS.", channel.Name);
                    await liveTranscode.StopAsync(slug, "kick");
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
    /// Misma lógica que el reproductor de Starvios: el prefijo del playback_id decide el CDN
    /// (cf = Cloudflare Stream, bn = Bunny, resto = Mux).
    /// </summary>
    private static string StarviosHlsUrl(string playbackId)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(playbackId, "^cf[0-9a-f]{32}$"))
            return $"https://customer-v4n77b7tsunuc58z.cloudflarestream.com/{playbackId[2..]}/manifest/video.m3u8";

        if (System.Text.RegularExpressions.Regex.IsMatch(playbackId, "^bn[0-9a-f]{32}$"))
        {
            var t = playbackId[2..];
            return $"https://vz-aa9d5a9c-a51.b-cdn.net/{t[..8]}-{t[8..12]}-{t[12..16]}-{t[16..20]}-{t[20..]}/playlist.m3u8";
        }

        return $"https://stream.mux.com/{playbackId}.m3u8";
    }

    private const string StarviosLiveNowUrl ="https://starvios.com/api/public/live-now?top=500";

    /// <summary>
    /// Starvios no tiene plugin de streamlink: se consulta su API pública de directos
    /// y se retransmite el HLS de Mux (stream.mux.com/{playback_id}.m3u8) vía ffmpeg.
    /// </summary>
    private async Task CheckStarviosAsync(
        List<Sepius.Domain.Entities.Channel> channels,
        ILiveTranscodeService liveTranscode,
        CancellationToken ct)
    {
        Dictionary<string, string> live;   // username → playback_id
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SepiusMonitor)");
            using var doc = System.Text.Json.JsonDocument.Parse(
                await http.GetStringAsync(StarviosLiveNowUrl, ct));

            live = new(StringComparer.OrdinalIgnoreCase);
            foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                if (row.TryGetProperty("status", out var st) && st.GetString() != "live") continue;
                if (!row.TryGetProperty("playback_id", out var pb) || pb.GetString() is not { Length: > 0 } playback) continue;
                if (!row.TryGetProperty("profiles", out var prof) ||
                    !prof.TryGetProperty("username", out var un) || un.GetString() is not { } username) continue;
                live[username] = playback;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Estado desconocido: no tocar las sesiones activas.
            _logger.LogWarning(ex, "Starvios: no se pudo consultar live-now. Estado mantenido.");
            return;
        }

        foreach (var channel in channels)
        {
            var slug = channel.Name["starvios:".Length..];
            var isTranscoding = liveTranscode.IsTranscoding(slug, "starvios");

            if (live.TryGetValue(slug, out var playbackId))
            {
                if (isTranscoding) continue;
                _logger.LogInformation("'{Channel}' está en DIRECTO (Starvios). Iniciando HLS.", channel.Name);
                await liveTranscode.StartAsync(slug, "starvios", ct, StarviosHlsUrl(playbackId));
            }
            else if (isTranscoding)
            {
                _logger.LogInformation("'{Channel}' ha terminado el directo (Starvios). Deteniendo HLS.", channel.Name);
                await liveTranscode.StopAsync(slug, "starvios");
            }
        }
    }

    /// <summary>
    /// Usa streamlink para detectar si un canal de Kick está en directo.
    /// Streamlink tiene plugin nativo para Kick y resuelve autenticación/Cloudflare
    /// de forma transparente. Ejecuta: streamlink https://kick.com/{slug} --json
    /// y parsea la salida para ver si devuelve streams disponibles.
    /// Devuelve: true=live, false=offline, null=error/indeterminado.
    /// </summary>
    private async Task<bool?> IsKickChannelLiveAsync(string slug, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20)); // timeout duro

            var psi = new ProcessStartInfo
            {
                FileName               = _streamlinkOptions.ExecutablePath,
                Arguments              = $"https://kick.com/{slug} --json",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            // streamlink --json devuelve un objeto JSON. Si el canal está offline:
            //   { "error": "No playable streams found on this URL: ..." }
            // Si está en directo:
            //   { "streams": { "best": {...}, "720p60": {...}, ... } }
            if (stdout.Contains("No playable streams", StringComparison.OrdinalIgnoreCase) ||
                stdout.Contains("\"error\":", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Streamlink: '{Slug}' offline o sin streams.", slug);
                return false;
            }

            if (stdout.Contains("\"streams\":", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Streamlink: '{Slug}' está en directo.", slug);
                return true;
            }

            // Salida inesperada (p.ej. plugin no instalado, error de red)
            _logger.LogWarning("Streamlink: salida inesperada para '{Slug}': {Output}",
                slug, stdout.Length > 200 ? stdout[..200] : stdout);
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Streamlink: timeout comprobando directo de '{Slug}'. Estado mantenido.", slug);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streamlink: error comprobando directo de '{Slug}'.", slug);
            return null;
        }
    }
}
