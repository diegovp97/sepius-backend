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
<<<<<<< HEAD
        // Solo es ready si HAY un proceso activo Y el m3u8 existe con datos.
        // Sin esta condición, segmentos de sesiones anteriores harían que el
        // frontend cargara datos obsoletos sin lanzar un nuevo transcode.
        var key = MakeKey(NormalizePlatform(platform), Normalize(channelName));
        if (!_active.ContainsKey(key)) return false;
=======
>>>>>>> a2f783527fd4e29f8f0fc23a7dbdf1ed89cf91b9
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
                "Ya hay transcode activo para '{Key}'. Estado={Status}",
                key, existing.Status);
            return Task.CompletedTask;
        }

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
        var outputDir  = GetHlsDirectory(platform, channelName);
        var m3u8Path   = GetM3u8Path(platform, channelName);
        var segPattern = Path.Combine(outputDir, "seg%04d.ts");
        var streamUrl  = platform is "kick" ? $"kick.com/{channelName}" : $"twitch.tv/{channelName}";

        PrepareOutputDir(outputDir);

<<<<<<< HEAD
        // Prefijo único por sesión para evitar que el navegador sirva segmentos
        // de sesiones anteriores desde su caché HTTP.
        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var streamUrl = platform switch
        {
            "kick"   => $"https://kick.com/{channelName}",
            _        => $"https://www.twitch.tv/{channelName}",
        };

        // ── PROCESO 1: Streamlink → stdout ──────────────────────────────────
=======
>>>>>>> a2f783527fd4e29f8f0fc23a7dbdf1ed89cf91b9
        var slPsi = new ProcessStartInfo
        {
            FileName               = _options.ExecutablePath,
            Arguments              = $"{streamUrl} best --stdout",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

<<<<<<< HEAD
        // ── PROCESO 2: ffmpeg stdin → HLS ───────────────────────────────────
        var m3u8Path = GetM3u8Path(platform, channelName);
        var segPattern    = Path.Combine(outputDir, $"s{sessionId}_%04d.m4s");
        var initFilename  = $"s{sessionId}_init.mp4";   // relativo al directorio del m3u8

        var ffPsi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            Arguments = string.Join(" ",
                "-y",                                           // sobreescribir sin preguntar
                "-fflags +genpts",                             // regenerar PTS: el stream de Kick llega con timestamps irregulares
                "-i pipe:0",                                    // leer de stdin
                "-map 0:v",                                     // solo stream de video
                "-map 0:a",                                     // solo stream de audio (excluye timed_id3)
                // Re-encode con perfil/nivel EXPLÍCITOS. El transmuxer TS→fMP4 de hls.js
                // malinterpreta el codec del stream de Kick (lo lee como avc1.640028 High@4.0
                // a 1920x1080 cuando el contenido real es otro), provocando que Chrome rechace
                // el SourceBuffer (error 4). Generando fMP4 directamente desde ffmpeg, hls.js
                // NO transmuxea: reproduce los segmentos tal cual, con codec correcto.
                "-vf scale=1280:720",                           // 720p: menos píxeles que 1080p
                "-r 30",                                        // 30fps (real-time en CPU sin GPU)
                "-vsync cfr",                                   // frame rate constante: evita gaps de PTS de video
                "-c:v libx264",                                 // H.264
                "-profile:v main",                              // Main profile: codec string limpio (avc1.4d401f)
                "-level:v 3.1",                                 // nivel fijo y válido
                "-pix_fmt yuv420p",                             // 8-bit 4:2:0 (universal en browsers)
                "-preset ultrafast",                            // mínima CPU
                "-tune zerolatency",                            // sin B-frames: compat. MSE live
                "-crf 23",                                      // calidad estándar
                "-g 60",                                        // keyframe cada 60 frames (2s @ 30fps)
                "-keyint_min 60",                               // GOP fijo
                "-sc_threshold 0",                              // sin keyframes por escena
                "-c:a aac",                                     // AAC-LC
                "-b:a 128k",                                    // 128kbps audio
                "-ar 44100",                                    // sample rate fijo
                "-af aresample=async=1:first_pts=0",           // resamplea audio: rellena gaps y fija A/V sync (corrige 'Packet duration out of range')
                "-f hls",                                       // formato de salida HLS
                "-hls_segment_type fmp4",                       // fMP4/CMAF: hls.js NO transmuxea
                "-hls_time 2",                                  // segmentos de 2 s
                "-hls_list_size 6",                             // 6 segmentos en el manifest
                "-hls_flags delete_segments+append_list+omit_endlist", // live continuo
                $"-hls_fmp4_init_filename \"{initFilename}\"", // init segment relativo al m3u8
=======
        var ffPsi = new ProcessStartInfo
        {
            FileName              = _options.FfmpegPath,
            Arguments             = string.Join(" ",
                "-y",
                "-i pipe:0",
                "-c copy",
                "-f hls",
                "-hls_time 1",
                "-hls_list_size 6",
                "-hls_flags delete_segments+append_list",
>>>>>>> a2f783527fd4e29f8f0fc23a7dbdf1ed89cf91b9
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
                _logger.LogDebug("[{Ch}|sl] {L}", channelName, e.Data);

                // Detectar "No playable streams" — señal de que no hay directo
                if (e.Data.Contains("No playable streams found", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Streamlink: no hay directo activo para '{Channel}'. Abortando transcode.",
                        channelName);
                    session.Status = TranscodeStatus.Failed;
                    KillSession(session, key);
                }
            };

            ff.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) _logger.LogDebug("[{Ch}|ff] {L}", channelName, e.Data);
            };

            sl.Start();
            sl.BeginErrorReadLine();

            ff.Start();
            ff.BeginErrorReadLine();

            session.StreamlinkProcess = sl;
            session.FfmpegProcess     = ff;
            session.Status            = TranscodeStatus.Running;

            _logger.LogInformation(
                "Live transcode iniciado. Canal='{Key}' sl.PID={SlPid} ff.PID={FfPid}",
                key, sl.Id, ff.Id);

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
                "Proceso terminado para '{Key}'. sl.Exited={SlExited} ff.Exited={FfExited}",
                key, sl.HasExited, ff.HasExited);
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
            "Limpiando transcode para '{Key}'. Estado final={Status}",
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
