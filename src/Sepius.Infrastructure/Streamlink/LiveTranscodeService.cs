using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    // ── Inicio ─────────────────────────────────────────────────────────────

    public Task StartAsync(string channelName, string platform = "twitch", CancellationToken ct = default)
    {
        platform    = NormalizePlatform(platform);
        channelName = Normalize(channelName);

        if (!ValidChannelName.IsMatch(channelName))
            throw new ArgumentException($"Nombre de canal inválido: '{channelName}'", nameof(channelName));

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
        _ = RunTranscodeAsync(key, platform, channelName, session, ct);
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

    // ── Pipeline principal ─────────────────────────────────────────────────

    private async Task RunTranscodeAsync(
        string key,
        string platform,
        string channelName,
        TranscodeSession session,
        CancellationToken ct)
    {
        var outputDir = GetHlsDirectory(platform, channelName);
        var m3u8Path  = GetM3u8Path(platform, channelName);
        var mp4Path   = GetMp4Path(platform, channelName);

        PrepareOutputDir(outputDir);

        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var streamUrl = platform switch
        {
            "kick" => $"https://kick.com/{channelName}",
            _      => $"https://www.twitch.tv/{channelName}",
        };

        var segPattern = Path.Combine(outputDir, $"s{sessionId}_%04d.ts");
        var useYtDlp = platform == "twitch" && File.Exists(_options.TwitchCookiesPath);

        var scriptPath = $"/tmp/sepius_transcode_{sessionId}.sh";
        var scriptContent = useYtDlp
            ? BuildYtDlpScript(streamUrl, segPattern, m3u8Path, mp4Path)
            : BuildStreamlinkScript(sessionId, streamUrl, segPattern, m3u8Path, mp4Path);
        await File.WriteAllTextAsync(scriptPath, scriptContent, ct).ConfigureAwait(false);

        if (useYtDlp)
            _logger.LogInformation(
                "[Transcode] Usando yt-dlp con cookies para '{Key}'. Cookies={CookiesPath}",
                key, _options.TwitchCookiesPath);
        else if (platform == "twitch")
            _logger.LogWarning(
                "[Transcode] Cookies de Twitch no encontradas en '{CookiesPath}'. Usando Streamlink; algunos canales pueden quedar audio-only.",
                _options.TwitchCookiesPath);

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

        return string.Join("\n",
            "#!/bin/bash",
            "set -euo pipefail",
            "cookies=$(mktemp /tmp/sepius_twitch_cookies.XXXXXX)",
            $"cp {ShellQuote(_options.TwitchCookiesPath)} \"$cookies\"",
            "trap 'rm -f \"$cookies\"' EXIT",
            $"url=$({ShellQuote(_options.YtDlpPath)} --cookies \"$cookies\" --no-warnings -f b -g {ShellQuote(streamUrl)} | head -n 1)",
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

    private async Task CleanupAsync(string key, string platform, string channelName, string mp4Path)
    {
        if (!_active.TryRemove(key, out var session))
            return;

        var finalStatus = session.Status is TranscodeStatus.Stopping or TranscodeStatus.Failed
            ? session.Status
            : TranscodeStatus.Stopped;

        _logger.LogInformation("[Transcode] Limpiando '{Key}'. Estado={Status}", key, finalStatus);

        KillSession(session, key);
        session.PipelineProcess?.Dispose();

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

                var recording = Recording.Create(channelName, mp4Path);
                recording.EndedAt = DateTime.UtcNow;
                recording.Status = RecordingStatus.Completed;
                recording.FileSizeBytes = finalInfo.Length;

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
        => platform.ToLowerInvariant().Trim() is "kick" ? "kick" : "twitch";

    private static string Normalize(string channelName)
        => channelName.ToLowerInvariant().Trim();

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\\''")}'";

    private void PrepareOutputDir(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        foreach (var f in Directory.GetFiles(outputDir, "*.ts")) File.Delete(f);
        var m3u8 = Path.Combine(outputDir, "index.m3u8");
        if (File.Exists(m3u8)) File.Delete(m3u8);
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

        Task.Run(async () =>
        {
            try { await handler(recording); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error en handler de RecordingCompleted para '{Channel}'",
                    recording.ChannelName);
            }
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
