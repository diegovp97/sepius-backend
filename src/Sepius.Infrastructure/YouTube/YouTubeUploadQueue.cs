using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.YouTube;

public sealed class UploadJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public required Recording Recording { get; init; }
    public UploadStatus Status { get; set; } = UploadStatus.Queued;
    public string? VideoId { get; set; }
    public string? Error { get; set; }
    public DateTime QueuedAt { get; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum UploadStatus
{
    Queued,
    Uploading,
    Completed,
    Failed
}

public sealed class YouTubeUploadQueue : BackgroundService
{
    private readonly ConcurrentQueue<UploadJob> _queue = new();
    private readonly ConcurrentDictionary<string, UploadJob> _jobs = new();
    private readonly IYouTubeUploadService _youtubeUpload;
    private readonly ILogger<YouTubeUploadQueue> _logger;

    public YouTubeUploadQueue(
        IYouTubeUploadService youtubeUpload,
        ILogger<YouTubeUploadQueue> logger)
    {
        _youtubeUpload = youtubeUpload;
        _logger = logger;
    }

    public UploadJob Enqueue(Recording recording)
    {
        var job = new UploadJob { Recording = recording };
        _jobs[job.Id] = job;
        _queue.Enqueue(job);
        _logger.LogInformation("Upload queued: {JobId} for '{File}'", job.Id, recording.FileName);
        return job;
    }

    public UploadJob? GetJob(string jobId)
        => _jobs.GetValueOrDefault(jobId);

    public IReadOnlyList<UploadJob> GetAllJobs()
        => _jobs.Values.OrderByDescending(j => j.QueuedAt).ToList();

    public IEnumerable<UploadJob> GetActiveJobs()
        => _jobs.Values.Where(j => j.Status is UploadStatus.Queued or UploadStatus.Uploading);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("YouTube upload queue started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_queue.TryDequeue(out var job))
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            else
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(UploadJob job, CancellationToken ct)
    {
        job.Status = UploadStatus.Uploading;
        job.StartedAt = DateTime.UtcNow;
        _logger.LogInformation("Starting upload: {JobId} for '{File}'", job.Id, job.Recording.FileName);

        try
        {
            var videoId = await _youtubeUpload.UploadAsync(job.Recording, ct);

            if (videoId is null)
            {
                job.Status = UploadStatus.Failed;
                job.Error = "YouTube returned null — revisa los logs del backend.";
            }
            else
            {
                job.Status = UploadStatus.Completed;
                job.VideoId = videoId;
                _logger.LogInformation("Upload completed: {JobId} → {VideoId}", job.Id, videoId);
            }
        }
        catch (Exception ex)
        {
            job.Status = UploadStatus.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "Upload failed: {JobId}", job.Id);
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
        }
    }
}
