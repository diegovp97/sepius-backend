using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;

namespace Sepius.Infrastructure.Streamlink;

/// <summary>
/// Un clip es una copia (sin recodificar) de los últimos segmentos del HLS en vivo. El HLS conserva ~90 s,
/// así que se pueden cortar clips de hasta 60 s. Cuando <c>behindSeconds</c> es mayor que 0 el clip termina
/// esos segundos antes del borde live: es lo que ve el espectador, que va por detrás del último segmento.
/// </summary>
public sealed class ClipService(IOptions<StreamlinkOptions> options, ILogger<ClipService> logger) : IClipService
{
    public const int MinSeconds = 5;
    public const int MaxSeconds = 60;
    public const int MaxBehindSeconds = 20;

    private static readonly string[] Platforms = ["twitch", "kick", "starvios"];
    private static readonly Regex ChannelName = new(@"^[a-z0-9_]{1,25}$", RegexOptions.Compiled);
    private static readonly Regex SegmentName = new(@"^s\d{6,12}_\d{4}\.ts$", RegexOptions.Compiled);

    private readonly StreamlinkOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(2, 2); // como mucho 2 cortes a la vez

    public static string ClipsDirectory(StreamlinkOptions o) => Path.Combine(o.OutputPath, "clips");

    public async Task<ClipCut?> CutAsync(string channel, string platform, int seconds, int behindSeconds, CancellationToken ct = default)
    {
        channel  = (channel ?? "").Trim().ToLowerInvariant();
        platform = (platform ?? "").Trim().ToLowerInvariant();
        if (!ChannelName.IsMatch(channel)) throw new ArgumentException("Canal inválido.", nameof(channel));
        if (!Platforms.Contains(platform)) throw new ArgumentException("Plataforma inválida.", nameof(platform));
        seconds       = Math.Clamp(seconds, MinSeconds, MaxSeconds);
        behindSeconds = Math.Clamp(behindSeconds, 0, MaxBehindSeconds);

        var liveDir = Path.Combine(_o.OutputPath, "live", platform, channel);
        var m3u8    = Path.Combine(liveDir, "index.m3u8");
        if (!File.Exists(m3u8)) return null;

        var segments = ParsePlaylist(await File.ReadAllLinesAsync(m3u8, ct).ConfigureAwait(false));

        // Desde el final: saltar behindSeconds y luego juntar seconds.
        var i = segments.Count - 1;
        for (double skipped = 0; i >= 0 && skipped < behindSeconds; i--) skipped += segments[i].Duration;

        var chosen = new List<(string Name, double Duration)>();
        double total = 0;
        for (; i >= 0 && total < seconds; i--)
        {
            if (!File.Exists(Path.Combine(liveDir, segments[i].Name))) break; // ya borrado: no hay más hacia atrás
            chosen.Insert(0, segments[i]);
            total += segments[i].Duration;
        }
        if (total < MinSeconds) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id    = Guid.NewGuid().ToString("N");
            var dir   = ClipsDirectory(_o);
            Directory.CreateDirectory(dir);
            var list  = Path.Combine(dir, id + ".txt");
            var mp4   = Path.Combine(dir, id + ".mp4");
            var thumb = Path.Combine(dir, id + ".jpg");

            try
            {
                // Los nombres ya están validados por SegmentName: no pueden contener comillas ni separadores.
                await File.WriteAllLinesAsync(list,
                    chosen.Select(s => "file '" + Path.Combine(liveDir, s.Name).Replace('\\', '/') + "'"), ct).ConfigureAwait(false);

                var ok = await RunFfmpegAsync(["-v", "error", "-nostats", "-y", "-f", "concat", "-safe", "0", "-i", list,
                                               "-c", "copy", "-movflags", "+faststart", mp4], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                if (!ok || !File.Exists(mp4) || new FileInfo(mp4).Length == 0)
                {
                    TryDelete(mp4);
                    logger.LogWarning("[Clip] ffmpeg no pudo cortar el clip de '{Channel}'.", channel);
                    return null;
                }

                var hasThumb = await RunFfmpegAsync(["-v", "error", "-nostats", "-y", "-ss", "0.5", "-i", mp4,
                                                     "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", thumb],
                                                    TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)
                               && File.Exists(thumb);

                logger.LogInformation("[Clip] {Id}: {Channel}/{Platform}, {Secs:F1} s ({Segs} segmentos, {Size:N0} bytes).",
                    id, channel, platform, total, chosen.Count, new FileInfo(mp4).Length);
                return new ClipCut(id, Math.Round(total, 1), id + ".mp4", hasThumb ? id + ".jpg" : null);
            }
            finally { TryDelete(list); }
        }
        finally { _gate.Release(); }
    }

    private static List<(string Name, double Duration)> ParsePlaylist(string[] lines)
    {
        var result = new List<(string, double)>();
        double? pending = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var v = line["#EXTINF:".Length..].Split(',')[0];
                pending = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                if (pending is { } dur && SegmentName.IsMatch(line)) result.Add((line, dur));
                pending = null;
            }
        }
        return result;
    }

    private async Task<bool> RunFfmpegAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _o.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogWarning("[Clip|ffmpeg] {L}", e.Data);
        };
        proc.Start();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning("[Clip] ffmpeg superó el tiempo máximo ({Secs} s).", (int)timeout.TotalSeconds);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* sin importancia */ }
    }
}
