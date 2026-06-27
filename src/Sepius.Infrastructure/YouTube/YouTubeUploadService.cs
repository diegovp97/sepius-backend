using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.YouTube;

public sealed class YouTubeUploadService : IYouTubeUploadService
{
    private readonly YouTubeOptions _options;
    private readonly ILogger<YouTubeUploadService> _logger;

    public YouTubeUploadService(
        IOptions<YouTubeOptions> options,
        ILogger<YouTubeUploadService> logger)
    {
        _options = options.Value;
        _logger = logger;
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

            var credential = GoogleCredential.FromRefreshToken(
                _options.RefreshToken,
                YouTubeService.Scope.YoutubeUpload)
                .CreateWithUserAgent("Sepius");

            var youtubeService = new YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Sepius"
            });

            var video = BuildVideoMetadata(recording);
            using var fileStream = new FileStream(
                recording.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 262_144,
                useAsync: true);

            var insertRequest = youtubeService.Videos.Insert(
                video,
                "snippet,status",
                fileStream,
                "video/*");

            insertRequest.ChunkSize = ResumableUpload.MinimumChunkSize * 4;
            insertRequest.ProgressChanged += progress => LogProgress(progress, recording.FileName);
            insertRequest.ResponseReceived += response =>
                _logger.LogInformation(
                    "YouTube upload OK: '{File}' → https://youtu.be/{VideoId}",
                    recording.FileName, response.Id);

            var result = await insertRequest.UploadAsync(ct);

            if (result.Status == UploadStatus.Failed)
            {
                _logger.LogError(result.Exception,
                    "YouTube upload failed for '{File}'.", recording.FileName);
                return null;
            }

            return insertRequest.ResponseBody?.Id;
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

    private Video BuildVideoMetadata(Recording recording)
    {
        var startedAt = recording.StartedAt.ToLocalTime();
        var title = $"{recording.ChannelName} - {startedAt:yyyy-MM-dd HH:mm}";
        var description =
            $"Stream de {recording.ChannelName} grabado el {startedAt:dd/MM/yyyy} a las {startedAt:HH:mm}.\n" +
            $"Grabado automáticamente por Sepius.";

        return new Video
        {
            Snippet = new VideoSnippet
            {
                Title = title.Length > 100 ? title[..100] : title,
                Description = description,
                Tags = [recording.ChannelName, "stream", "twitch", "grabacion"],
                CategoryId = "20" // Gaming
            },
            Status = new VideoStatus
            {
                PrivacyStatus = _options.PrivacyStatus
            }
        };
    }

    private void LogProgress(IUploadProgress progress, string fileName)
    {
        switch (progress.Status)
        {
            case UploadStatus.Uploading:
                _logger.LogDebug(
                    "YouTube uploading '{File}': {MB:F1} MB sent",
                    fileName, progress.BytesSent / 1_048_576.0);
                break;
            case UploadStatus.Failed:
                _logger.LogError(progress.Exception,
                    "Upload failed for '{File}'.", fileName);
                break;
        }
    }
}
