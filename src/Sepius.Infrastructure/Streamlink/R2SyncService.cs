using System.Collections.Concurrent;
using System.Text;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sepius.Infrastructure.Streamlink;

/// <summary>
/// Observa el directorio HLS local y sube segmentos .ts + manifest m3u8 a Cloudflare R2.
/// Usa AWSSDK.S3 v3.7 (compatible con R2, a diferencia de v4).
/// </summary>
public sealed class R2SyncService : BackgroundService
{
    private readonly R2Options _r2;
    private readonly StreamlinkOptions _sl;
    private readonly ILogger<R2SyncService> _logger;
    private readonly IAmazonS3 _s3;
    private readonly ConcurrentDictionary<string, HashSet<string>> _uploadedSegments = new();

    public R2SyncService(
        IOptions<R2Options> r2,
        IOptions<StreamlinkOptions> sl,
        ILogger<R2SyncService> logger)
    {
        _r2 = r2.Value;
        _sl = sl.Value;
        _logger = logger;

        var config = new AmazonS3Config
        {
            ServiceURL = _r2.EndpointUrl,
            ForcePathStyle = true,
            AuthenticationRegion = RegionEndpoint.USEast1.SystemName
        };
        _s3 = new AmazonS3Client(_r2.AccessKeyId, _r2.SecretAccessKey, config);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_r2.Enabled)
        {
            _logger.LogInformation("[R2] Deshabilitado (sin credenciales). HLS se sirve localmente.");
            return;
        }

        var liveDir = Path.Combine(_sl.OutputPath, "live");
        Directory.CreateDirectory(liveDir);

        _logger.LogInformation(
            "[R2] Sync activo. Bucket={Bucket} PublicUrl={Url} WatchDir={Dir}",
            _r2.BucketName, _r2.PublicBaseUrl, liveDir);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndUploadAsync(stoppingToken);
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[R2] Error en loop de sync");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ScanAndUploadAsync(CancellationToken ct)
    {
        var liveDir = Path.Combine(_sl.OutputPath, "live");
        if (!Directory.Exists(liveDir)) return;

        foreach (var m3u8 in Directory.GetFiles(liveDir, "index.m3u8", SearchOption.AllDirectories))
        {
            await UploadHlsManifestAsync(m3u8, ct);
        }
    }

    private async Task UploadHlsManifestAsync(string m3u8Path, CancellationToken ct)
    {
        try
        {
            var content = await File.ReadAllTextAsync(m3u8Path, ct);
            if (string.IsNullOrWhiteSpace(content)) return;

            var (platform, channel) = ExtractPathInfo(m3u8Path);
            if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(channel)) return;

            var key = $"{platform}:{channel}";
            var uploaded = _uploadedSegments.GetOrAdd(key, _ => new HashSet<string>());

            var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            var rewritten = new StringBuilder();
            var newSegments = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var r2Url = $"{_r2.PublicBaseUrl.TrimEnd('/')}/{platform}/{channel}/{trimmed}";
                    rewritten.AppendLine(r2Url);
                    if (uploaded.Add(trimmed))
                        newSegments.Add(trimmed);
                }
                else
                {
                    rewritten.AppendLine(line);
                }
            }

            // Subir m3u8 reescrito
            var m3u8Key = $"{platform}/{channel}/index.m3u8";
            var m3u8Bytes = Encoding.UTF8.GetBytes(rewritten.ToString());
            using var m3u8Stream = new MemoryStream(m3u8Bytes);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _r2.BucketName,
                Key = m3u8Key,
                InputStream = m3u8Stream,
                ContentType = "application/vnd.apple.mpegurl"
            }, ct);

            _logger.LogDebug("[R2] Uploaded m3u8: {Key}", m3u8Key);

            // Subir segmentos nuevos
            foreach (var seg in newSegments)
            {
                var localSeg = Path.Combine(Path.GetDirectoryName(m3u8Path)!, seg);
                if (File.Exists(localSeg))
                {
                    var segKey = $"{platform}/{channel}/{seg}";
                    await using var segStream = File.OpenRead(localSeg);
                    await _s3.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = _r2.BucketName,
                        Key = segKey,
                        InputStream = segStream,
                        ContentType = "video/mp2t"
                    }, ct);
                    _logger.LogDebug("[R2] Uploaded segment: {Key}", segKey);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[R2] Error subiendo m3u8: {Path}", m3u8Path);
        }
    }

    private static (string Platform, string Channel) ExtractPathInfo(string fullPath)
    {
        var parts = fullPath.Replace("\\", "/").Split('/');
        var liveIdx = Array.IndexOf(parts, "live");
        if (liveIdx < 0 || liveIdx + 2 >= parts.Length)
            return ("", "");
        return (parts[liveIdx + 1], parts[liveIdx + 2]);
    }
}
