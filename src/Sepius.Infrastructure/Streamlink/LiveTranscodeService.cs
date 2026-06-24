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
/// Por qué este enfoque resuelve el problema del ban:
/// - Streamlink accede al stream de Twitch de forma ANÓNIMA (Client Credentials,
///   no cuenta de usuario). El navegador ya no envía cookies de Twitch.
/// - ffmpeg convierte el stream a formato HLS (segmentos .ts + manifest .m3u8)
///   que se sirven como ficheros estáticos desde el backend.
/// - Angular los reproduce con hls.js, un <video> nativo sin ningún embed de Twitch.
///
/// NOTA DE DESARROLLO (Windows):
/// Requiere que ffmpeg esté instalado y en el PATH.
/// Descarga: https://ffmpeg.org/download.html
/// En Docker (producción) ya está instalado por el Dockerfile.
/// </summary>
public sealed class LiveTranscodeService : ILiveTranscodeService, IDisposable
{
    // Entrada: (streamlink process, ffmpeg process, tarea de pipe entre ambos)
    private readonly ConcurrentDictionary<string, (Process Sl, Process Ff, Task Pipe)> _active = new();

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

    public bool IsTranscoding(string channelName, string platform = "twitch")
        => _active.ContainsKey(MakeKey(platform, channelName));

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

    public Task StartAsync(string channelName, string platform = "twitch", CancellationToken ct = default)
    {
        platform   = NormalizePlatform(platform);
        channelName = Normalize(channelName);

        if (!ValidChannelName.IsMatch(channelName))
            throw new ArgumentException($"Nombre de canal inválido: '{channelName}'", nameof(channelName));

        var key = MakeKey(platform, channelName);
        if (_active.ContainsKey(key))
        {
            _logger.LogWarning("Ya hay un transcode activo para '{Key}'", key);
            return Task.CompletedTask;
        }

        var outputDir = GetHlsDirectory(platform, channelName);
        PrepareOutputDir(outputDir);

        // Prefijo único por sesión para evitar que el navegador sirva segmentos
        // de sesiones anteriores desde su caché HTTP.
        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var streamUrl = platform switch
        {
            "kick"   => $"https://kick.com/{channelName}",
            _        => $"https://www.twitch.tv/{channelName}",
        };

        // ── PROCESO 1: Streamlink → stdout ──────────────────────────────────
        var slPsi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            // --stdout: vuelca el stream al stdout en lugar de a un fichero
            Arguments = $"{streamUrl} best --stdout",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

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
                $"-hls_segment_filename \"{segPattern}\"",
                $"\"{m3u8Path}\""
            ),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var sl = new Process { StartInfo = slPsi, EnableRaisingEvents = true };
        var ff = new Process { StartInfo = ffPsi, EnableRaisingEvents = true };

        sl.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _logger.LogDebug("[{Ch}|sl] {L}", channelName, e.Data);
        };
        ff.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _logger.LogDebug("[{Ch}|ff] {L}", channelName, e.Data);
        };

        try
        {
            sl.Start();
            sl.BeginErrorReadLine();

            ff.Start();
            ff.BeginErrorReadLine();

            // Conectar stdout de streamlink con stdin de ffmpeg en un Task background.
            // CopyToAsync bombea los bytes entre los dos procesos sin bloquear.
            var pipeTask = sl.StandardOutput.BaseStream
                .CopyToAsync(ff.StandardInput.BaseStream)
                .ContinueWith(_ =>
                {
                    try { ff.StandardInput.Close(); } catch { }
                }, TaskScheduler.Default);

            _active[key] = (sl, ff, pipeTask);

            _logger.LogInformation(
                "Live transcode iniciado. Canal: '{Key}' | sl PID: {SlPid} | ff PID: {FfPid}",
                key, sl.Id, ff.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando transcode para '{Channel}'", channelName);
            sl.Dispose();
            ff.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(string channelName, string platform = "twitch")
    {
        var key = MakeKey(NormalizePlatform(platform), Normalize(channelName));

        if (_active.TryRemove(key, out var entry))
        {
            _logger.LogInformation("Deteniendo transcode de '{Key}'", key);
            KillSafely(entry.Sl, $"{key}-streamlink");
            KillSafely(entry.Ff, $"{key}-ffmpeg");
        }

        return Task.CompletedTask;
    }

    // ── HELPERS ──────────────────────────────────────────────────────────────

    private string GetHlsDirectory(string platform, string channelName)
        => Path.Combine(_options.OutputPath, "live", platform, channelName);

    private string GetM3u8Path(string platform, string channelName)
        => Path.Combine(GetHlsDirectory(platform, channelName), "index.m3u8");

    private static string MakeKey(string platform, string channelName)
        => $"{platform}:{channelName}";

    private static string NormalizePlatform(string platform)
        => platform.ToLowerInvariant().Trim() is "kick" ? "kick" : "twitch";

    private void PrepareOutputDir(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        // Limpiar segmentos de sesiones anteriores
        foreach (var f in Directory.GetFiles(outputDir, "*.ts")) File.Delete(f);
        var m3u8 = Path.Combine(outputDir, "index.m3u8");
        if (File.Exists(m3u8)) File.Delete(m3u8);
    }

    private void KillSafely(Process process, string label)
    {
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
        finally { process.Dispose(); }
    }

    private static string Normalize(string channelName)
        => channelName.ToLowerInvariant().Trim();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (channel, entry) in _active)
        {
            KillSafely(entry.Sl, $"{channel}-streamlink");
            KillSafely(entry.Ff, $"{channel}-ffmpeg");
        }

        _active.Clear();
    }
}
