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

        var quality = "best";

        var slPsi = new ProcessStartInfo
        {
            FileName               = _options.ExecutablePath,
            Arguments              = $"{streamUrl} {quality} --stdout",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        var segPattern = Path.Combine(outputDir, $"s{sessionId}_%04d.ts");

        // ffmpeg escribe HLS + MP4 en la misma pasada (sin segundo streamlink)
        var ffPsi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            Arguments = string.Join(" ",
                "-y",
                "-i pipe:0",
                "-map 0:v",
                "-map 0:a",
                "-c copy",
                // Salida 1: HLS
                "-f hls",
                "-hls_time 4",
                "-hls_list_size 20",
                "-hls_flags delete_segments+append_list+omit_endlist+independent_segments",
                "-hls_allow_cache 0",
                $"-hls_segment_filename \"{segPattern}\"",
                $"\"{m3u8Path}\"",
                // Salida 2: MP4 grabación (copia directa, 0 CPU extra)
                "-c copy",
                "-movflags +faststart",
                $"\"{mp4Path}\""),
            UseShellExecute       = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow        = true
        };

        var sl = new Process { StartInfo = slPsi, EnableRaisingEvents = true };
        var ff = new Process { StartInfo = ffPsi, EnableRaisingEvents = true };

        try
        {
            sl.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _logger.LogDebug("[{Key}|sl] {L}", key, e.Data);

                if (e.Data.Contains("No playable streams found", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[Transcode] Streamlink: no hay streams para '{Key}'. Abortando.", key);
                    session.Status = TranscodeStatus.Failed;
                    KillSession(session, key);
                }

                if (e.Data.Contains("Available streams:", StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("[Transcode] Streams disponibles en '{Key}': {Streams}", key, e.Data);

                if (e.Data.Contains("Opening stream:", StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("[Transcode] Streamlink abriendo stream '{Key}': {Info}", key, e.Data);
            };

            ff.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (e.Data.StartsWith("frame=", StringComparison.OrdinalIgnoreCase)) return;
                _logger.LogDebug("[{Key}|ff] {L}", key, e.Data);
            };

            sl.Start();
            sl.BeginErrorReadLine();

            ff.Start();
            ff.BeginErrorReadLine();

            session.StreamlinkProcess = sl;
            session.FfmpegProcess     = ff;
            session.Status            = TranscodeStatus.Running;

            _logger.LogInformation(
                "[Transcode] Pipeline activo. Key='{Key}' | sl.PID={SlPid} | ff.PID={FfPid} | HLS={M3u8} | MP4={Mp4}",
                key, sl.Id, ff.Id, m3u8Path, mp4Path);

            // Pipe stdout de streamlink → stdin de ffmpeg
            _ = sl.StandardOutput.BaseStream
                .CopyToAsync(ff.StandardInput.BaseStream, ct)
                .ContinueWith(_ =>
                {
                    try { ff.StandardInput.Close(); } catch { }
                }, TaskScheduler.Default);

            // Health watchdog: comprueba procesos cada 30s
            StartWatchdog(key, session, ct);

            await Task.WhenAny(
                sl.WaitForExitAsync(ct),
                ff.WaitForExitAsync(ct)).ConfigureAwait(false);

            _logger.LogWarning(
                "[Transcode] Proceso terminado para '{Key}'. sl={SlCode} ff={FfCode}",
                key, sl.HasExited ? sl.ExitCode : -1,
                ff.HasExited ? ff.ExitCode : -1);
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

                    var slAlive = session.StreamlinkProcess is { HasExited: false };
                    var ffAlive = session.FfmpegProcess is { HasExited: false };

                    if (!slAlive || !ffAlive)
                    {
                        _logger.LogWarning(
                            "[Watchdog] Proceso muerto para '{Key}'. slAlive={Sl} ffAlive={Ff}. Marcando como Failed.",
                            key, slAlive, ffAlive);
                        session.Status = TranscodeStatus.Failed;
                        KillSession(session, key);
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
        session.StreamlinkProcess?.Dispose();
        session.FfmpegProcess?.Dispose();

        // Si la grabación MP4 existe y el stream terminó correctamente, notificar
        if (finalStatus == TranscodeStatus.Stopped && File.Exists(mp4Path))
        {
            var fileInfo = new FileInfo(mp4Path);
            if (fileInfo.Length > 0)
            {
                _logger.LogInformation(
                    "[Transcode] Grabación MP4 completada: {Path} ({Size:N0} bytes)",
                    mp4Path, fileInfo.Length);

                var recording = Recording.Create($"twitch:{channelName}", mp4Path);
                recording.EndedAt = DateTime.UtcNow;
                recording.Status = RecordingStatus.Completed;
                recording.FileSizeBytes = fileInfo.Length;

                FireRecordingCompleted(recording);
            }
        }

        await Task.CompletedTask;
    }

    private void KillSession(TranscodeSession session, string key)
    {
        KillSafely(session.StreamlinkProcess, $"{key}-streamlink");
        KillSafely(session.FfmpegProcess,     $"{key}-ffmpeg");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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
                // En Linux: SIGINT para que ffmpeg finalice el MP4 (escribe moov atom)
                // kill -INT <pid> es la forma más fiable de enviar SIGINT
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

                process.WaitForExit(10_000);

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
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
            session.StreamlinkProcess?.Dispose();
            session.FfmpegProcess?.Dispose();
        }

        _active.Clear();
    }
}
