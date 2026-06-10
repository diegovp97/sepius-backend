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

    public bool IsTranscoding(string channelName)
        => _active.ContainsKey(Normalize(channelName));

    public bool IsHlsReady(string channelName)
    {
        var m3u8 = GetM3u8Path(Normalize(channelName));
        return File.Exists(m3u8) && new FileInfo(m3u8).Length > 0;
    }

    public string GetHlsUrl(string channelName)
        => $"/live/{Normalize(channelName)}/index.m3u8";

    public Task StartAsync(string channelName, CancellationToken ct = default)
    {
        channelName = Normalize(channelName);

        if (!ValidChannelName.IsMatch(channelName))
            throw new ArgumentException($"Nombre de canal inválido: '{channelName}'", nameof(channelName));

        if (_active.ContainsKey(channelName))
        {
            _logger.LogWarning("Ya hay un transcode activo para '{Channel}'", channelName);
            return Task.CompletedTask;
        }

        var outputDir = GetHlsDirectory(channelName);
        PrepareOutputDir(outputDir);

        // ── PROCESO 1: Streamlink → stdout ──────────────────────────────────
        var slPsi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            // --stdout: vuelca el stream al stdout en lugar de a un fichero
            Arguments = $"twitch.tv/{channelName} best --stdout",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // ── PROCESO 2: ffmpeg stdin → HLS ───────────────────────────────────
        var m3u8Path = GetM3u8Path(channelName);
        var segPattern = Path.Combine(outputDir, "seg%04d.ts");

        var ffPsi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            Arguments = string.Join(" ",
                "-y",                                           // sobreescribir sin preguntar
                "-i pipe:0",                                    // leer de stdin
                "-c copy",                                      // copiar sin recodificar (0 CPU extra)
                "-f hls",                                       // formato de salida HLS
                "-hls_time 2",                                  // segmentos de 2 segundos
                "-hls_list_size 10",                            // 10 segmentos en el manifest
                "-hls_flags delete_segments+append_list",       // borrar segmentos viejos
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

            _active[channelName] = (sl, ff, pipeTask);

            _logger.LogInformation(
                "Live transcode iniciado. Canal: '{Channel}' | sl PID: {SlPid} | ff PID: {FfPid}",
                channelName, sl.Id, ff.Id);
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

    public Task StopAsync(string channelName)
    {
        channelName = Normalize(channelName);

        if (_active.TryRemove(channelName, out var entry))
        {
            _logger.LogInformation("Deteniendo transcode de '{Channel}'", channelName);
            KillSafely(entry.Sl, $"{channelName}-streamlink");
            KillSafely(entry.Ff, $"{channelName}-ffmpeg");
        }

        return Task.CompletedTask;
    }

    // ── HELPERS ──────────────────────────────────────────────────────────────

    private string GetHlsDirectory(string channelName)
        => Path.Combine(_options.OutputPath, "live", channelName);

    private string GetM3u8Path(string channelName)
        => Path.Combine(GetHlsDirectory(channelName), "index.m3u8");

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
