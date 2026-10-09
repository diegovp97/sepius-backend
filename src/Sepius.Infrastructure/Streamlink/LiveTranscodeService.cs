using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.Streamlink;

/// <summary>
/// Pipeline optimizado: streamlink (1 proceso) → ffmpeg → HLS + MP4 simultáneamente.
///
/// OPTIMIZACIONES vs versión anterior:
///   1. UN solo streamlink por canal (antes eran 2: uno para HLS, otro para .mp4).
///   2. ffmpeg escribe HLS + MP4 en la misma pasada usando salidas múltiples.
///   3. HLS: hls_time=4, hls_list_size=10 (más buffer, menos cortes).
///   4. Health monitor: watchdog task que detecta procesos muertos y auto-reinicia.
///   5. RecordingCompleted event integrado (ya no hace falta StreamlinkService separado).
/// </summary>
public sealed class LiveTranscodeService : ILiveTranscodeService, IDisposable
{
    private readonly ConcurrentDictionary<string, TranscodeSession> _active = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _watchdogs = new();

    private readonly StreamlinkOptions _options;
    private readonly ILogger<LiveTranscodeService> _logger;
    private bool _disposed;
    private int _pendingHandlers; // handlers de RecordingCompleted aún en ejecución

    private static readonly Regex ValidChannelName =
        new(@"^[a-z0-9_]{1,25}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public event Func<Recording, Task>? RecordingCompleted;

    public LiveTranscodeService(IOptions<StreamlinkOptions> options, ILogger<LiveTranscodeService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // ── Consultas públicas ─────────────────────────────────────────────────

    public bool IsTranscoding(string channelName, string platform = "twitch")
    {
        var key = MakeKey(NormalizePlatform(platform), Normalize(channelName));
        return _active.TryGetValue(key, out var s)
            && s.Status is TranscodeStatus.Starting or TranscodeStatus.Running;
    }

    public bool IsHlsReady(string channelName, string platform = "twitch")
    {
        var key = MakeKey(NormalizePlatform(platform), Normalize(channelName));
        if (!_active.ContainsKey(key)) return false;

        var m3u8 = GetM3u8Path(NormalizePlatform(platform), Normalize(channelName));
        return File.Exists(m3u8) && new FileInfo(m3u8).Length > 0;
    }

    public string GetHlsUrl(string channelName, string platform = "twitch")
    {
        return $"/live/{NormalizePlatform(platform)}/{Normalize(channelName)}/index.m3u8";
    }

    public IReadOnlyList<LiveSessionDto> GetActiveSessions()
        => _active
            .Where(kv => kv.Value.Status is TranscodeStatus.Starting or TranscodeStatus.Running)
            .Select(kv => new LiveSessionDto(
                kv.Key.Split(':')[0], kv.Value.Channel, kv.Value.StartedAt, kv.Value.Status.ToString()))
            .OrderBy(x => x.StartedAt)
            .ToList();

    // ── Inicio ─────────────────────────────────────────────────────────────

    public Task StartAsync(string channelName, string platform = "twitch", CancellationToken ct = default, string? sourceUrl = null)
    {
        platform    = NormalizePlatform(platform);
        channelName = Normalize(channelName);

        if (!ValidChannelName.IsMatch(channelName))
            throw new ArgumentException($"Nombre de canal inválido: '{channelName}'", nameof(channelName));

        if (platform == "starvios" && !IsAllowedStarviosSource(sourceUrl))
            throw new ArgumentException("starvios requiere una sourceUrl HTTPS de stream.mux.com.", nameof(sourceUrl));

        var key     = MakeKey(platform, channelName);
        var session = new TranscodeSession(channelName);

        if (!_active.TryAdd(key, session))
        {
            var existing = _active[key];
            _logger.LogWarning(
                "[Transcode] Ya hay sesión activa para '{Key}'. Estado={Status}. Ignorando.",
                key, existing.Status);
            return Task.CompletedTask;
        }

        _logger.LogInformation("[Transcode] Arrancando pipeline optimizado para '{Key}'...", key);
        _ = RunTranscodeAsync(key, platform, channelName, session, sourceUrl, ct);
        return Task.CompletedTask;
    }

    // ── Parada ─────────────────────────────────────────────────────────────

    public Task StopAsync(string channelName, string platform = "twitch")
    {
        var key = MakeKey(NormalizePlatform(platform), Normalize(channelName));

        if (_active.TryGetValue(key, out var session))
        {
            _logger.LogInformation("Deteniendo transcode de '{Key}'", key);
            session.Status = TranscodeStatus.Stopping;
            KillSession(session, key);
        }

        return Task.CompletedTask;
    }

    public async Task StopAllAsync(TimeSpan timeout)
    {
        var sessions = _active.ToArray();
        if (sessions.Length == 0 && Volatile.Read(ref _pendingHandlers) == 0) return;

        _logger.LogInformation("[Transcode] Apagando: cierre limpio de {Count} sesión(es)…", sessions.Length);

        // KillSession bloquea hasta que ffmpeg cierra el MP4: se lanza en paralelo.
        var kills = sessions.Select(kv => Task.Run(() =>
        {
            kv.Value.Status = TranscodeStatus.Stopping;
            KillSession(kv.Value, kv.Key);
        })).ToArray();

        // Se espera a que acaben los procesos, la limpieza (faststart) y los handlers
        // (que apuntan la grabación en la cola de subida) antes de dejar que el host se cierre.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline &&
               (!_active.IsEmpty || Volatile.Read(ref _pendingHandlers) > 0 || kills.Any(k => !k.IsCompleted)))
            await Task.Delay(250).ConfigureAwait(false);

        if (!_active.IsEmpty)
            _logger.LogWarning("[Transcode] Quedaban {Count} sesión(es) al agotar el tiempo de apagado.", _active.Count);
    }

    // ── Pipeline principal ─────────────────────────────────────────────────

    private async Task RunTranscodeAsync(
        string key,
        string platform,
        string channelName,
        TranscodeSession session,
        string? sourceUrl,
        CancellationToken ct)
    {
        var outputDir = GetHlsDirectory(platform, channelName);
        var m3u8Path  = GetM3u8Path(platform, channelName);
        // Starvios: solo retransmisión HLS, sin grabación MP4 (así no entra en la cola de subida a YouTube).
        var isStarvios = platform == "starvios";
        var mp4Path    = isStarvios ? "" : GetMp4Path(platform, channelName);

        PrepareOutputDir(outputDir);

        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var streamUrl = platform switch
        {
            "kick" => $"https://kick.com/{channelName}",
            _      => $"https://www.twitch.tv/{channelName}",
        };

        var segPattern = Path.Combine(outputDir, $"s{sessionId}_%04d.ts");
        var isTwitch = platform == "twitch";

        var scriptPath = $"/tmp/sepius_transcode_{sessionId}.sh";
        var scriptContent = isStarvios
            ? BuildDirectHlsScript(sourceUrl!, segPattern, m3u8Path)
            : isTwitch
                ? BuildTwitchScript(sessionId, streamUrl, segPattern, m3u8Path, mp4Path)
                : BuildStreamlinkScript(sessionId, streamUrl, segPattern, m3u8Path, mp4Path);
        await File.WriteAllTextAsync(scriptPath, scriptContent, ct).ConfigureAwait(false);

        if (isTwitch)
            _logger.LogInformation(
                "[Transcode] Twitch '{Key}': Streamlink si ofrece vídeo; si no, yt-dlp (cookies de respaldo: {CookiesPath}, existe={CookiesExist})",
                key, _options.TwitchCookiesPath, File.Exists(_options.TwitchCookiesPath));

        var psi = new ProcessStartInfo
        {
            FileName               = "/bin/bash",
            Arguments              = scriptPath,
            UseShellExecute        = false,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (e.Data.StartsWith("frame=", StringComparison.OrdinalIgnoreCase)) return;

                if (e.Data.Contains("Available streams:", StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("[Transcode] Streams disponibles en '{Key}': {Streams}", key, e.Data);
                else if (e.Data.Contains("Opening stream:", StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("[Transcode] Streamlink abriendo stream '{Key}': {Info}", key, e.Data);
                else if (e.Data.Contains("No playable streams found", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[Transcode] Streamlink: no hay streams para '{Key}'. Abortando.", key);
                    session.Status = TranscodeStatus.Failed;
                }
                else
                    _logger.LogDebug("[{Key}|pipe] {L}", key, e.Data);
            };

            proc.Start();
            proc.BeginErrorReadLine();

            session.PipelineProcess = proc;
            session.Status          = TranscodeStatus.Running;

            _logger.LogInformation(
                "[Transcode] Pipeline activo. Key='{Key}' | PID={Pid} | HLS={M3u8} | MP4={Mp4}",
                key, proc.Id, m3u8Path, mp4Path);

            StartWatchdog(key, session, ct);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.HasExited && proc.ExitCode != 0 && session.Status == TranscodeStatus.Running)
                session.Status = TranscodeStatus.Failed;

            _logger.LogWarning(
                "[Transcode] Proceso terminado para '{Key}'. exit={ExitCode}",
                key, proc.HasExited ? proc.ExitCode : -1);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Transcode cancelado para '{Key}'.", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en el pipeline de transcode para '{Key}'.", key);
            session.Status = TranscodeStatus.Failed;
        }
        finally
        {
            StopWatchdog(key);
            await CleanupAsync(key, platform, channelName, mp4Path).ConfigureAwait(false);
        }
    }

    // ── Health Watchdog ────────────────────────────────────────────────────

    private string BuildStreamlinkScript(
        long sessionId,
        string streamUrl,
        string segPattern,
        string m3u8Path,
        string mp4Path)
    {
        var quality = string.IsNullOrWhiteSpace(_options.Quality) ? "best" : _options.Quality.Trim();
        // Si el canal emite una sola calidad con nombre atípico (p. ej. "1280p"), "best" no la encuentra
        // ("The specified stream(s) 'best' could not be found") y ffmpeg recibe una tubería vacía.
        // best-unfiltered sí la incluye; Streamlink prueba la lista separada por comas en orden.
        if (quality == "best") quality = "best,best-unfiltered";
        var additionalArgs = string.IsNullOrWhiteSpace(_options.AdditionalArgs) ? string.Empty : $" {_options.AdditionalArgs.Trim()}";
        var slLogPath = $"/tmp/sl_{sessionId}.log";

        var slCmd = string.Join(" ",
            ShellQuote(_options.ExecutablePath) + additionalArgs,
            ShellQuote(streamUrl),
            ShellQuote(quality),
            "--stdout",
            $"2>{ShellQuote(slLogPath)}");

        var ffArgs = string.Join(" ",
            "-y",
            "-fflags +discardcorrupt+genpts",
            "-analyzeduration 10000000",
            "-probesize 5000000",
            "-i pipe:0",
            "-map 0:v?",
            "-map 0:a?",
            "-c copy",
            "-f hls",
            "-hls_time 4",
            "-hls_list_size 20",
            "-hls_flags delete_segments+append_list+omit_endlist+independent_segments",
            "-max_muxing_queue_size 1024",
            $"-hls_segment_filename {ShellQuote(segPattern)}",
            ShellQuote(m3u8Path),
            "-c copy",
            "-max_muxing_queue_size 1024",
            ShellQuote(mp4Path));

        return string.Join("\n",
            "#!/bin/bash",
            "set -o pipefail",
            $"{slCmd} | {ShellQuote(_options.FfmpegPath)} {ffArgs}");
    }

    /// <summary>Starvios: ffmpeg copia directamente el HLS de Mux (sin streamlink y sin MP4).</summary>
    private string BuildDirectHlsScript(string sourceUrl, string segPattern, string m3u8Path)
    {
        const string inputFlags = "-y -fflags +discardcorrupt+genpts -rw_timeout 15000000 -analyzeduration 10000000 -probesize 5000000";
        var hlsOut = string.Join(" ",
            "-c copy",
            "-f hls",
            "-hls_time 4",
            "-hls_list_size 20",
            "-hls_flags delete_segments+append_list+omit_endlist+independent_segments",
            "-max_muxing_queue_size 1024",
            $"-hls_segment_filename {ShellQuote(segPattern)}",
            ShellQuote(m3u8Path));
        var ffmpeg = ShellQuote(_options.FfmpegPath);
        var src    = ShellQuote(sourceUrl);

        // El master de Starvios ofrece varias calidades (240p-1080p). Sin elegir, "-c copy" las mete
        // todas en cada segmento, así que se elige la de mayor BANDWIDTH.
        //
        // Cloudflare Stream sirve el audio como una pista aparte (#EXT-X-MEDIA:TYPE=AUDIO con URI). Con
        // "-map 0:p:N" sobre el master, el ffmpeg 6.1 de la imagen dejaba un stream de audio declarado pero
        // sin paquetes (0 canales, 0 Hz): el reproductor no veía audio. Por eso, si hay pista de audio separada,
        // se abren la variante de vídeo y la de audio como dos entradas y se mapean explícitamente.
        // Si no la hay (audio ya mezclado, p. ej. Mux), se mantiene el mapeo por programa de antes.
        return $$"""
#!/bin/bash
set -o pipefail
SRC={{src}}
master=$(curl -fsS --max-time 15 "$SRC" 2>/dev/null) || master=""
base="${SRC%/*}/"
resolve() { case "$1" in http*) echo "$1";; *) echo "$base$1";; esac; }

best=$(printf '%s\n' "$master" | awk '
  /^#EXT-X-STREAM-INF/ { bw=0; if (match($0, /[:,]BANDWIDTH=[0-9]+/)) { s=substr($0,RSTART,RLENGTH); sub(/.*=/,"",s); bw=s+0 } want=1; next }
  want==1 && NF && $0 !~ /^#/ { if (bw>m) { m=bw; u=$0 } want=0 }
  END { print u }')
audio=$(printf '%s\n' "$master" | grep '^#EXT-X-MEDIA:TYPE=AUDIO' | grep -o 'URI="[^"]*"' | head -1 | sed 's/^URI="//; s/"$//')

if [ -n "$best" ] && [ -n "$audio" ]; then
  echo "[Transcode] Starvios: vídeo y audio como entradas separadas" >&2
  exec {{ffmpeg}} {{inputFlags}} -i "$(resolve "$best")" -i "$(resolve "$audio")" -map 0:v:0 -map 1:a:0 {{hlsOut}}
fi

idx=$(printf '%s\n' "$master" | grep EXT-X-STREAM-INF \
  | sed -E 's/.*[^-]BANDWIDTH=([0-9]+).*/\1/' \
  | awk '{if ($1+0 > m) {m = $1+0; i = NR-1} } END {if (m > 0) print i}') || true
if [ -n "$idx" ]; then map="-map 0:p:$idx"; else map="-map 0:v? -map 0:a?"; fi
echo "[Transcode] Starvios: map=$map" >&2
exec {{ffmpeg}} {{inputFlags}} -i {{src}} $map {{hlsOut}}
""";
    }

    /// <summary>
    /// Twitch: Streamlink (copia directa, sin transcodificar) si ofrece algún stream con vídeo;
    /// si solo hay audio, yt-dlp (con cookies de respaldo).
    /// </summary>
    private string BuildTwitchScript(
        long sessionId,
        string streamUrl,
        string segPattern,
        string m3u8Path,
        string mp4Path)
    {
        static string Body(string script) => string.Join("\n", script.Split('\n').Skip(1));

        var additionalArgs = string.IsNullOrWhiteSpace(_options.AdditionalArgs) ? string.Empty : $" {_options.AdditionalArgs.Trim()}";
        var hasVideo = $"timeout 30 {ShellQuote(_options.ExecutablePath)}{additionalArgs} --json {ShellQuote(streamUrl)} 2>/dev/null | grep -qE '\"[0-9]+p[0-9]*'";

        // Al empezar un directo, Twitch tarda unos segundos en ofrecer vídeo (primero solo audio o el anuncio
        // inicial). Si se decide de golpe, yt-dlp se queda con el clip del anuncio ("Commercial break in
        // progress", ~16 s, que además termina). Por eso se espera a que Streamlink vea vídeo antes de caer a yt-dlp.
        const int attempts = 18; // 18 × ~5 s ≈ 90 s
        return string.Join("\n",
            "#!/bin/bash",
            "ok=0",
            $"for i in $(seq 1 {attempts}); do",
            $"  if {hasVideo}; then ok=1; break; fi",
            $"  echo \"[Transcode] Streamlink aún sin vídeo (intento $i/{attempts}); espero 5 s…\" >&2",
            "  sleep 5",
            "done",
            "if [ \"$ok\" = 1 ]; then",
            "  echo \"[Transcode] Streamlink ofrece vídeo; usando copia directa.\" >&2",
            Body(BuildStreamlinkScript(sessionId, streamUrl, segPattern, m3u8Path, mp4Path)),
            "else",
            "  echo \"[Transcode] Streamlink sin vídeo tras esperar; usando yt-dlp.\" >&2",
            Body(BuildYtDlpScript(streamUrl, segPattern, m3u8Path, mp4Path)),
            "fi");
    }

    private string BuildYtDlpScript(
        string streamUrl,
        string segPattern,
        string m3u8Path,
        string mp4Path)
    {
        var height = Math.Clamp(_options.YtDlpTranscodeHeight, 240, 1080);
        var teeTarget = string.Join("|",
            $"[f=hls:hls_time=4:hls_list_size=20:hls_flags=delete_segments+append_list+omit_endlist+independent_segments:hls_segment_filename={segPattern}]{m3u8Path}",
            $"[f=mp4:movflags=+faststart]{mp4Path}");

        var ffArgs = string.Join(" ",
            "-y",
            "-fflags +discardcorrupt+genpts",
            "-rw_timeout 15000000",
            "-analyzeduration 10000000",
            "-probesize 5000000",
            "-i \"$url\"",
            "-map 0:v:0",
            "-map 0:a:0?",
            $"-vf scale=-2:{height}",
            "-c:v libx264",
            "-preset ultrafast",
            "-tune zerolatency",
            "-pix_fmt yuv420p",
            "-c:a aac",
            "-b:a 128k",
            "-ac 2",
            "-max_muxing_queue_size 1024",
            "-f tee",
            ShellQuote(teeTarget));

        // Primero sin cookies: los directos públicos dan vídeo así y unas cookies caducadas
        // hacen que Twitch responda "subscriber-only". Las cookies solo se usan de respaldo.
        var ytDlp = ShellQuote(_options.YtDlpPath);
        return string.Join("\n",
            "#!/bin/bash",
            "set -uo pipefail",
            "cookies=$(mktemp /tmp/sepius_twitch_cookies.XXXXXX)",
            "trap 'rm -f \"$cookies\"' EXIT",
            $"url=$({ytDlp} --no-warnings -f b -g {ShellQuote(streamUrl)} | head -n 1)",
            $"if [ -z \"$url\" ] && [ -f {ShellQuote(_options.TwitchCookiesPath)} ]; then",
            "  echo \"[yt-dlp] Sin cookies no hay vídeo; reintentando con cookies.\" >&2",
            $"  cp {ShellQuote(_options.TwitchCookiesPath)} \"$cookies\"",
            $"  url=$({ytDlp} --cookies \"$cookies\" --no-warnings -f b -g {ShellQuote(streamUrl)} | head -n 1)",
            "fi",
            "if [ -z \"$url\" ]; then",
            "  echo \"[yt-dlp] No se pudo resolver URL HLS con vídeo.\" >&2",
            "  exit 1",
            "fi",
            $"exec {ShellQuote(_options.FfmpegPath)} {ffArgs}");
    }

    private void StartWatchdog(string key, TranscodeSession session, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _watchdogs[key] = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);

                    if (session.Status != TranscodeStatus.Running) break;

                    var alive = session.PipelineProcess is { HasExited: false };

                    if (!alive)
                    {
                        _logger.LogWarning(
                            "[Watchdog] Proceso muerto para '{Key}'. Marcando como Failed.",
                            key);
                        session.Status = TranscodeStatus.Failed;
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Watchdog] Error para '{Key}'", key);
                }
            }
        }, cts.Token);
    }

    private void StopWatchdog(string key)
    {
        if (_watchdogs.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ── Limpieza ───────────────────────────────────────────────────────────

    /// <summary>
    /// Limpieza de una sesión terminada (faststart + notificar). Cuenta como trabajo pendiente desde la
    /// primera línea: StopAllAsync espera a que llegue a cero, y así el apagado no corta el faststart ni
    /// deja la grabación sin apuntar. FireRecordingCompleted incrementa su propio contador antes de que este
    /// decremente, de modo que no hay hueco en el que parezca que todo ha terminado.
    /// </summary>
    private async Task CleanupAsync(string key, string platform, string channelName, string mp4Path)
    {
        Interlocked.Increment(ref _pendingHandlers);
        try { await CleanupCoreAsync(key, platform, channelName, mp4Path).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _pendingHandlers); }
    }

    private async Task CleanupCoreAsync(string key, string platform, string channelName, string mp4Path)
    {
        if (!_active.TryRemove(key, out var session))
            return;

        var finalStatus = session.Status is TranscodeStatus.Stopping or TranscodeStatus.Failed
            ? session.Status
            : TranscodeStatus.Stopped;

        _logger.LogInformation("[Transcode] Limpiando '{Key}'. Estado={Status}", key, finalStatus);

        KillSession(session, key);
        session.PipelineProcess?.Dispose();
        ScheduleHlsCleanup(key, platform, channelName);

        // Si la grabación MP4 existe y el stream terminó correctamente, notificar
        if (finalStatus is TranscodeStatus.Stopped or TranscodeStatus.Stopping && File.Exists(mp4Path))
        {
            var fileInfo = new FileInfo(mp4Path);
            if (fileInfo.Length > 0)
            {
                // Aplicar faststart como post-proceso para que el moov atom quede al inicio
                var repaired = await ApplyFaststartAsync(mp4Path).ConfigureAwait(false);
                if (repaired)
                    _logger.LogInformation(
                        "[Transcode] Faststart aplicado a: {Path}", mp4Path);

                var finalInfo = new FileInfo(mp4Path);
                _logger.LogInformation(
                    "[Transcode] Grabación MP4 completada: {Path} ({Size:N0} bytes)",
                    mp4Path, finalInfo.Length);

                // FromFile toma el inicio real del nombre del fichero (yyyyMMdd_HHmmss, UTC). Con Create()
                // StartedAt valdría "ahora" (el final del directo): duración ≈ 0 y título con la hora de fin.
                var recording = Recording.FromFile(channelName, mp4Path);
                recording.EndedAt = DateTime.UtcNow;

                FireRecordingCompleted(recording);
            }
        }

        await Task.CompletedTask;
    }

    private void KillSession(TranscodeSession session, string key)
    {
        KillSafely(session.PipelineProcess, $"{key}-pipeline");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<bool> ApplyFaststartAsync(string mp4Path)
    {
        var tempPath = mp4Path + ".faststart.mp4";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.FfmpegPath,
                Arguments = $"-y -i \"{mp4Path}\" -c copy -movflags +faststart \"{tempPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            await proc.WaitForExitAsync().ConfigureAwait(false);

            if (proc.ExitCode == 0 && File.Exists(tempPath))
            {
                var tempInfo = new FileInfo(tempPath);
                if (tempInfo.Length > 0)
                {
                    File.Delete(mp4Path);
                    File.Move(tempPath, mp4Path);
                    return true;
                }
            }

            if (File.Exists(tempPath)) File.Delete(tempPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error aplicando faststart a '{Path}'", mp4Path);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return false;
        }
    }

    private string GetHlsDirectory(string platform, string channelName)
        => Path.Combine(_options.OutputPath, "live", platform, channelName);

    private string GetM3u8Path(string platform, string channelName)
        => Path.Combine(GetHlsDirectory(platform, channelName), "index.m3u8");

    private string GetMp4Path(string platform, string channelName)
    {
        var dir = Path.Combine(_options.OutputPath, platform, channelName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp4");
    }

    private static string MakeKey(string platform, string channelName)
        => $"{platform}:{channelName}";

    private static string NormalizePlatform(string platform)
        => platform.ToLowerInvariant().Trim() switch
        {
            "kick"     => "kick",
            "starvios" => "starvios",
            _          => "twitch",
        };

    // La URL acaba dentro de un script bash: solo se acepta el formato exacto de cada CDN de Starvios
    // (Mux, Cloudflare Stream o Bunny, según el prefijo del playback_id).
    private static readonly (string Host, Regex Path)[] StarviosSources =
    [
        ("stream.mux.com", new(@"^/[A-Za-z0-9]{10,100}\.m3u8$", RegexOptions.Compiled)),
        ("customer-v4n77b7tsunuc58z.cloudflarestream.com", new(@"^/[0-9a-f]{32}/manifest/video\.m3u8$", RegexOptions.Compiled)),
        ("vz-aa9d5a9c-a51.b-cdn.net", new(@"^/[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}/playlist\.m3u8$", RegexOptions.Compiled)),
    ];

    private static bool IsAllowedStarviosSource(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && u.Scheme == Uri.UriSchemeHttps
           && u.Query.Length == 0
           && StarviosSources.Any(s => s.Host == u.Host && s.Path.IsMatch(u.AbsolutePath));

    private static string Normalize(string channelName)
        => channelName.ToLowerInvariant().Trim();

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\\''")}'";

    private void PrepareOutputDir(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        // removeEmptyDir: false — si no, el directorio recién creado (o vaciado) se borraba aquí mismo
        // y ffmpeg fallaba con "Failed to open file ... No such file or directory" (HLS en 404).
        DeleteHlsFiles(outputDir, removeEmptyDir: false);
    }

    /// <summary>Borra solo los segmentos (.ts) y el index.m3u8 de un directorio HLS, y el directorio si queda vacío.</summary>
    private void DeleteHlsFiles(string dir, bool removeEmptyDir = true)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.ts")) File.Delete(f);
            var m3u8 = Path.Combine(dir, "index.m3u8");
            if (File.Exists(m3u8)) File.Delete(m3u8);
            if (removeEmptyDir && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Transcode] No se pudo limpiar el HLS de '{Dir}'.", dir);
        }
    }

    /// <summary>
    /// Tras acabar un directo, el HLS se borra a los 2 minutos (margen para que los espectadores
    /// terminen de ver los últimos segmentos). Si el canal vuelve a emitir antes, no se toca nada.
    /// </summary>
    private void ScheduleHlsCleanup(string key, string platform, string channelName)
    {
        var dir = GetHlsDirectory(platform, channelName);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            if (_active.ContainsKey(key)) return;
            DeleteHlsFiles(dir);
            _logger.LogInformation("[Transcode] HLS de '{Key}' borrado (directo terminado).", key);
        });
    }

    /// <summary>
    /// Al arrancar el grabador no hay ninguna sesión: todo lo que quede en live/ son restos
    /// (de canales que ya no se vigilan o de un reinicio). Solo se tocan ficheros HLS.
    /// </summary>
    public void PurgeStaleHls()
    {
        var root = Path.Combine(_options.OutputPath, "live");
        if (!Directory.Exists(root)) return;

        foreach (var platformDir in Directory.GetDirectories(root))
            foreach (var channelDir in Directory.GetDirectories(platformDir))
                DeleteHlsFiles(channelDir);

        _logger.LogInformation("[Transcode] Restos de HLS anteriores borrados al arrancar.");
    }

    private void KillSafely(Process? process, string label)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                // 1) SIGINT a todos los hijos directos (ffmpeg, streamlink)
                //    para que ffmpeg finalice y escriba el moov atom.
                try
                {
                    var pkillPsi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "pkill",
                        Arguments = $"-INT -P {process.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var pkillProc = System.Diagnostics.Process.Start(pkillPsi);
                    pkillProc?.WaitForExit(3_000);
                }
                catch { }

                // 2) SIGINT al proceso bash
                try
                {
                    var killPsi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "kill",
                        Arguments = $"-INT {process.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var killProc = System.Diagnostics.Process.Start(killPsi);
                    killProc?.WaitForExit(3_000);
                }
                catch { }

                process.WaitForExit(120_000);

                if (!process.HasExited)
                {
                    _logger.LogWarning(
                        "[KillSafely] Proceso '{Label}' no terminó tras 120s. Forzando kill.",
                        label);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex) { _logger.LogError(ex, "Error matando proceso '{Label}'", label); }
    }

    private void FireRecordingCompleted(Recording recording)
    {
        var handler = RecordingCompleted;
        if (handler is null) return;

        Interlocked.Increment(ref _pendingHandlers);
        Task.Run(async () =>
        {
            try { await handler(recording); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error en handler de RecordingCompleted para '{Channel}'",
                    recording.ChannelName);
            }
            finally { Interlocked.Decrement(ref _pendingHandlers); }
        });
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (key, cts) in _watchdogs)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _watchdogs.Clear();

        foreach (var (key, session) in _active)
        {
            KillSession(session, key);
            session.PipelineProcess?.Dispose();
        }

        _active.Clear();
    }
}
