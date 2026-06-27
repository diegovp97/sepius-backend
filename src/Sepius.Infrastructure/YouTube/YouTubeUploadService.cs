using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.YouTube;

public sealed class YouTubeUploadService : IYouTubeUploadService
{
    private readonly YouTubeOptions _options;
    private readonly ILogger<YouTubeUploadService> _logger;
    private readonly HttpClient _http;

    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string UploadUrl = "https://www.googleapis.com/upload/youtube/v3/videos";

    public YouTubeUploadService(
        IOptions<YouTubeOptions> options,
        ILogger<YouTubeUploadService> logger,
        HttpClient http)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;
    }

    public async Task<string?> UploadAsync(Recording recording, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("YouTube upload disabled.");
            return null;
        }

        if (!File.Exists(recording.FilePath))
        {
            _logger.LogWarning("Cannot upload '{File}': file not found.", recording.FilePath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.RefreshToken))
        {
            _logger.LogError("YouTube:RefreshToken not configured.");
            return null;
        }

        try
        {
            _logger.LogInformation(
                "Starting YouTube upload: '{File}' ({SizeMB:F1} MB)",
                recording.FileName,
                recording.FileSizeBytes / 1_048_576.0);

            var accessToken = await GetAccessTokenAsync(ct);
            if (accessToken is null)
            {
                _logger.LogError("Failed to obtain YouTube access token.");
                return null;
            }

            var metadata = BuildMetadataJson(recording);

            using var fileStream = new FileStream(
                recording.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 262_144,
                useAsync: true);

            var initRequest = new HttpRequestMessage(HttpMethod.Post,
                $"{UploadUrl}?uploadType=resumable&part=snippet,status");
            initRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            initRequest.Headers.Add("X-Upload-Content-Length", fileStream.Length.ToString());
            initRequest.Headers.Add("X-Upload-Content-Type", "video/*");
            initRequest.Content = new StringContent(metadata, Encoding.UTF8, "application/json");

            var initResponse = await _http.SendAsync(initRequest, ct);
            if (!initResponse.IsSuccessStatusCode)
            {
                var error = await initResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError("YouTube resumable init failed ({Status}): {Error}", initResponse.StatusCode, error);
                return null;
            }

            var uploadUri = initResponse.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(uploadUri))
            {
                _logger.LogError("No upload URI returned from YouTube.");
                return null;
            }

            _logger.LogInformation("YouTube resumable upload started for '{File}'.", recording.FileName);

            var chunkSize = 10 * 1024 * 1024;
            var buffer = new byte[chunkSize];
            long offset = 0;

            while (offset < fileStream.Length)
            {
                ct.ThrowIfCancellationRequested();

                var bytesRead = await fileStream.ReadAsync(buffer, 0, chunkSize, ct);
                if (bytesRead == 0) break;

                var lastByte = offset + bytesRead - 1;
                var contentRange = $"bytes {offset}-{lastByte}/{fileStream.Length}";

                using var chunkContent = new ByteArrayContent(buffer, 0, bytesRead);
                chunkContent.Headers.ContentType = new MediaTypeHeaderValue("video/*");
                chunkContent.Headers.ContentRange = new ContentRangeHeaderValue(offset, lastByte, fileStream.Length);

                var chunkRequest = new HttpRequestMessage(HttpMethod.Put, uploadUri);
                chunkRequest.Content = chunkContent;
                chunkRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var chunkResponse = await _http.SendAsync(chunkRequest, ct);

                var sentMB = (offset + bytesRead) / 1_048_576.0;
                var totalMB = fileStream.Length / 1_048_576.0;
                _logger.LogDebug("YouTube uploading '{File}': {Sent:F1}/{Total:F1} MB",
                    recording.FileName, sentMB, totalMB);

                if (chunkResponse.StatusCode == System.Net.HttpStatusCode.OK ||
                    chunkResponse.StatusCode == System.Net.HttpStatusCode.Created)
                {
                    var responseText = await chunkResponse.Content.ReadAsStringAsync(ct);
                    var responseDoc = JsonDocument.Parse(responseText);
                    var videoId = responseDoc.RootElement.GetProperty("id").GetString();

                    _logger.LogInformation(
                        "YouTube upload OK: '{File}' → https://youtu.be/{VideoId}",
                        recording.FileName, videoId);

                    return videoId;
                }

                if ((int)chunkResponse.StatusCode != 308)
                {
                    var error = await chunkResponse.Content.ReadAsStringAsync(ct);
                    _logger.LogError("YouTube upload chunk failed ({Status}): {Error}",
                        chunkResponse.StatusCode, error);
                    return null;
                }

                offset += bytesRead;
            }

            _logger.LogError("YouTube upload completed without response for '{File}'.", recording.FileName);
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("YouTube upload cancelled for '{File}'.", recording.FileName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error uploading '{File}' to YouTube.", recording.FileName);
            return null;
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["refresh_token"] = _options.RefreshToken,
            ["grant_type"] = "refresh_token"
        };

        var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("YouTube token refresh failed ({Status}): {Error}", response.StatusCode, error);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    private string BuildMetadataJson(Recording recording)
    {
        var startedAt = recording.StartedAt.ToLocalTime();
        var title = $"{recording.ChannelName} - {startedAt:yyyy-MM-dd HH:mm}";
        if (title.Length > 100) title = title[..100];

        var description =
            $"Stream de {recording.ChannelName} grabado el {startedAt:dd/MM/yyyy} a las {startedAt:HH:mm}.\n" +
            $"Grabado automáticamente por Sepius.";

        var metadata = new
        {
            snippet = new
            {
                title,
                description,
                tags = new[] { recording.ChannelName, "stream", "twitch", "grabacion" },
                categoryId = "20"
            },
            status = new
            {
                privacyStatus = _options.PrivacyStatus
            }
        };

        return JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
