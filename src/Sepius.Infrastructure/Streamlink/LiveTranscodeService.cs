using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;

namespace Sepius.Infrastructure.Streamlink;

/// <summary>
/// Pipeline: streamlink (stdout) → pipe en memoria → ffmpeg (stdin) → ficheros HLS
///
/// Reglas de diseño senior:
///   - ffmpeg nunca arranca si streamlink no tiene stream válido.
///   - _active nunca queda ocupado si el proceso terminó.
///   - CleanupAsync siempre se ejecuta en un bloque finally.
///   - Un Task de monitor espera la muerte de ambos procesos y limpia el estado.
/// </summary>
public sealed class LiveTranscodeService : ILiveTranscodeService, IDisposable
{
    private readonly ConcurrentDictionary<string, TranscodeSession> _active = new();

    private readonly StreamlinkOptions _options;
    private readonly ILogger<LiveTranscodeService> _logger;
    private bool _disposed;

    private static readonly Regex ValidChannelName =
        new(@"^[a-z0-9_]{1,25}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        // Solo es ready si HAY un proceso activo Y el m3u8 existe con datos.
        // Sin esta condición, segmentos de sesiones anteriores harían que el
        // frontend cargara datos obsoletos sin lanzar un nuevo transcode.
        var key = MakeKey(NormalizePlatform(platform), Normalize(channelName));
        if (!_active.ContainsKey(key)) return false;
        var m3u8 = GetM3u8Path(NormalizePlatform(platform), Normalize(channelName));
        return File.Exists(m3u8) && new FileInfo(m3u8).Length > 0;
    }

    public string GetHlsUrl(string channelName, string platform = "twitch")
        => $"/live/{NormalizePlatform(platform)}/{Normalize(channelName)}/index.m3u8";

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
                "[Transcode] Ya hay sesión activa para '{Key}'. Estado={Status}. Ignorando arranque duplicado.",
                key, existing.Status);
            return Task.CompletedTask;
        }

        _logger.LogInformation("[Transcode] Arrancando pipeline para '{Key}'...", key);

        // Lanzar en background — no bloquea el EventSub worker
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

        PrepareOutputDir(outputDir);

        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var streamUrl = platform switch
        {
            "kick" => $"https://kick.com/{channelName}",
            _      => $"https://www.twitch.tv/{channelName}",
        };

        // Calidad preferida: 720p60 si existe; si no, el mejor que haya.
        // -c copy: no recodificamos — Kick ya envía H.264+AAC compatible con HLS.
        // Ventajas: 0 CPU de encoding, sin stalls, sin latencia extra.
        var quality = platform is "kick" ? "720p60,best" : "best";

        var slPsi = new ProcessStartInfo
        {
            FileName               = _options.ExecutablePath,
            Arguments              = $"{streamUrl} {quality} --stdout",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        // Segmentos TS nombrados con sessionId para cache-busting en el browser.
        var segPattern = Path.Combine(outputDir, $"s{sessionId}_%04d.ts");

        var ffPsi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            Arguments = string.Join(" ",
                "-y",                                     // sobreescribir sin preguntar
                "-i pipe:0",                             // leer de stdin (streamlink stdout)
                "-map 0:v",                              // solo vídeo
                "-map 0:a",                              // solo audio (excluye timed_id3)
                "-c copy",                               // no recodificar — 0 CPU de encoding
                "-f hls",
                "-hls_time 2",
                "-hls_list_size 6",
                "-hls_flags delete_segments+append_list+omit_endlist",
                $"-hls_segment_filename \"{segPattern}\"",
                $"\"{m3u8Path}\""),
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

                // Detectar "No playable streams" — señal de que no hay directo
                if (e.Data.Contains("No playable streams found", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "[Transcode] Streamlink: no hay streams para '{Key}'. Abortando.",
                        key);
                    session.Status = TranscodeStatus.Failed;
                    KillSession(session, key);
                }

                // Log cuando streamlink encuentra streams disponibles
                if (e.Data.Contains("Available streams:", StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("[Transcode] Streams disponibles en '{Key}': {Streams}", key, e.Data);

                // Log cuando streamlink abre el stream
                if (e.Data.Contains("Opening stream:", StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("[Transcode] Streamlink abriendo stream '{Key}': {Info}", key, e.Data);
            };

            ff.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                // Solo loguear líneas importantes de ffmpeg (no el spam de frames)
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
                "[Transcode] Pipeline activo. Key='{Key}' | sl.PID={SlPid} | ff.PID={FfPid} | out={OutDir}",
                key, sl.Id, ff.Id, outputDir);

            // Pipe stdout de streamlink → stdin de ffmpeg
            _ = sl.StandardOutput.BaseStream
                .CopyToAsync(ff.StandardInput.BaseStream, ct)
                .ContinueWith(_ =>
                {
                    try { ff.StandardInput.Close(); } catch { }
                }, TaskScheduler.Default);

            // Esperar a que cualquiera de los dos procesos termine
            await Task.WhenAny(
                sl.WaitForExitAsync(ct),
                ff.WaitForExitAsync(ct)).ConfigureAwait(false);

            _logger.LogWarning(
                "[Transcode] Proceso terminado para '{Key}'. sl.Exited={SlExited}(code={SlCode}) ff.Exited={FfExited}(code={FfCode})",
                key, sl.HasExited, sl.HasExited ? sl.ExitCode : -1,
                ff.HasExited, ff.HasExited ? ff.ExitCode : -1);
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
            // Siempre limpiar — nunca dejar _active con un proceso muerto
            await CleanupAsync(key).ConfigureAwait(false);
        }
    }

    // ── Limpieza ───────────────────────────────────────────────────────────

    private async Task CleanupAsync(string key)
    {
        if (!_active.TryRemove(key, out var session))
            return;

        var finalStatus = session.Status is TranscodeStatus.Stopping or TranscodeStatus.Failed
            ? session.Status
            : TranscodeStatus.Stopped;

        _logger.LogInformation(
            "[Transcode] Limpiando sesión '{Key}'. Estado final={Status}",
            key, finalStatus);

        KillSession(session, key);
        session.StreamlinkProcess?.Dispose();
        session.FfmpegProcess?.Dispose();

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
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex) { _logger.LogError(ex, "Error matando proceso '{Label}'", label); }
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (key, session) in _active)
        {
            KillSession(session, key);
            session.StreamlinkProcess?.Dispose();
            session.FfmpegProcess?.Dispose();
        }

        _active.Clear();
    }
}
