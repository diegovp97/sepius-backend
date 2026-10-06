using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.Drive;
using Sepius.Infrastructure.Pipeline;

namespace Sepius.Infrastructure.YouTube;

public sealed class UploadJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public required Recording Recording { get; init; }
    public UploadStatus Status { get; set; } = UploadStatus.Queued;
    public string? VideoId { get; set; }
    public string? Error { get; set; }

    /// <summary>Estado del respaldo en Drive: null (no aplica), Uploading, Done, Failed.</summary>
    public string? DriveStatus { get; set; }
    public string? DriveFileId { get; set; }
    public string? DriveError { get; set; }

    /// <summary>Reabre el paso de YouTube aunque ya constara como hecho (subida manual desde el panel).</summary>
    public bool Force { get; init; }

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

/// <summary>
/// Cola del pipeline de cada grabación: Google Drive (respaldo) → YouTube.
/// - Drive va primero, pero si falla YouTube se sube igualmente.
/// - El estado de cada paso se guarda en Postgres: un reinicio o un fallo retoma solo lo pendiente.
/// - Cada 30 min se revisa si quedó algo sin completar (hasta MaxAttempts intentos por paso).
/// </summary>
public sealed class YouTubeUploadQueue : BackgroundService
{
    internal const int MaxAttempts = 5;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly ConcurrentQueue<UploadJob> _queue = new();
    private readonly ConcurrentDictionary<string, UploadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, string> _activeByPath = new(); // ruta → jobId

    private readonly IYouTubeUploadService _youtubeUpload;
    private readonly IDriveUploadService _driveUpload;
    private readonly UploadPipelineStore _store;
    private readonly YouTubeOptions _youtubeOptions;
    private readonly GoogleDriveOptions _driveOptions;
    private readonly ILogger<YouTubeUploadQueue> _logger;

    public YouTubeUploadQueue(
        IYouTubeUploadService youtubeUpload,
        IDriveUploadService driveUpload,
        UploadPipelineStore store,
        IOptions<YouTubeOptions> youtubeOptions,
        IOptions<GoogleDriveOptions> driveOptions,
        ILogger<YouTubeUploadQueue> logger)
    {
        _youtubeUpload = youtubeUpload;
        _driveUpload = driveUpload;
        _store = store;
        _youtubeOptions = youtubeOptions.Value;
        _driveOptions = driveOptions.Value;
        _logger = logger;
    }

    public UploadJob Enqueue(Recording recording, bool force = false)
    {
        // Un fichero = un trabajo: si ya está en cola o subiéndose, se devuelve el existente.
        if (_activeByPath.TryGetValue(recording.FilePath, out var activeId) && _jobs.TryGetValue(activeId, out var active))
            return active;

        var job = new UploadJob { Recording = recording, Force = force };
        _jobs[job.Id] = job;
        _activeByPath[recording.FilePath] = job.Id;
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
        _logger.LogInformation(
            "Upload queue started. Drive={Drive} YouTube={YouTube}",
            _driveOptions.Enabled, _youtubeOptions.Enabled);

        // El primer barrido recupera lo que quedó a medias antes de un reinicio.
        var nextSweep = DateTime.UtcNow;
        var firstSweep = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= nextSweep)
            {
                await SweepAsync(includeRecent: firstSweep, stoppingToken);
                firstSweep = false;
                nextSweep = DateTime.UtcNow + SweepInterval;
            }

            if (_queue.TryDequeue(out var job))
                await ProcessJobAsync(job, stoppingToken);
            else
                await Task.Delay(1000, stoppingToken);
        }
    }

    /// <summary>Reencola las grabaciones con pasos pendientes o fallidos que aún tienen intentos disponibles.</summary>
    private async Task SweepAsync(bool includeRecent, CancellationToken ct)
    {
        try
        {
            // Tras un reinicio se retoma todo; en los barridos periódicos se deja un respiro tras cada fallo.
            var idleSince = includeRecent ? DateTime.UtcNow : DateTime.UtcNow - RetryAfter;
            var rows = await _store.ListIncompleteAsync(_driveOptions.Enabled, _youtubeOptions.Enabled, MaxAttempts, idleSince, ct);

            foreach (var row in rows)
            {
                if (!File.Exists(row.FilePath))
                {
                    _logger.LogWarning("Pipeline: '{File}' ya no existe en disco; se omite.", row.FilePath);
                    continue;
                }

                var rec = Recording.Create(row.ChannelName, row.FilePath);
                rec.Status = RecordingStatus.Completed;
                rec.EndedAt = File.GetLastWriteTimeUtc(row.FilePath);
                rec.FileSizeBytes = new FileInfo(row.FilePath).Length;

                _logger.LogInformation(
                    "Pipeline: retomando '{File}' (Drive={Drive}, YouTube={YouTube})",
                    rec.FileName, row.DriveStatus, row.YouTubeStatus);
                Enqueue(rec);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline: error en el barrido de recuperación.");
        }
    }

    private async Task ProcessJobAsync(UploadJob job, CancellationToken ct)
    {
        job.Status = UploadStatus.Uploading;
        job.StartedAt = DateTime.UtcNow;
        var rec = job.Recording;
        _logger.LogInformation("Starting pipeline: {JobId} for '{File}'", job.Id, rec.FileName);

        UploadPipeline? state = null;
        try
        {
            state = await _store.GetOrCreateAsync(rec, resetYouTube: job.Force, ct);
        }
        catch (Exception ex)
        {
            // Sin base de datos el pipeline sigue funcionando, solo que sin memoria entre reinicios.
            _logger.LogError(ex, "Pipeline: no se pudo leer/guardar el estado de '{File}'.", rec.FileName);
        }

        try
        {
            await RunDriveStepAsync(job, state, ct);
            await RunYouTubeStepAsync(job, state, ct);
        }
        catch (Exception ex)
        {
            job.Status = UploadStatus.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "Pipeline failed: {JobId}", job.Id);
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            _activeByPath.TryRemove(rec.FilePath, out _);
        }
    }

    /// <summary>Paso 1: copia de seguridad en Drive. Un fallo aquí NO impide subir a YouTube.</summary>
    private async Task RunDriveStepAsync(UploadJob job, UploadPipeline? state, CancellationToken ct)
    {
        if (!_driveOptions.Enabled)
            return;

        if (state?.DriveFileId is { } doneId)
        {
            job.DriveStatus = "Done";
            job.DriveFileId = doneId;
            return;
        }

        job.DriveStatus = "Uploading";
        string? fileId = null;
        string? error = null;
        try
        {
            fileId = await _driveUpload.UploadAsync(job.Recording, ct);
            if (fileId is null)
                error = "Drive devolvió null — revisa los logs del backend.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "Drive upload failed: {JobId}", job.Id);
        }

        job.DriveFileId = fileId;
        job.DriveError = error;
        job.DriveStatus = fileId is null ? "Failed" : "Done";
        await SaveSafelyAsync(() => _store.SaveDriveAsync(
            job.Recording.FilePath,
            fileId is null ? PipelineStepStatus.Failed : PipelineStepStatus.Done,
            fileId, error, ct), job);

        if (fileId is null)
            _logger.LogWarning("Drive falló para '{File}'; se continúa con YouTube.", job.Recording.FileName);
    }

    /// <summary>Paso 2: publicación en YouTube.</summary>
    private async Task RunYouTubeStepAsync(UploadJob job, UploadPipeline? state, CancellationToken ct)
    {
        if (!_youtubeOptions.Enabled)
        {
            job.Status = UploadStatus.Failed;
            job.Error = "YouTube está deshabilitado (YouTube__Enabled=false).";
            return;
        }

        if (state?.YouTubeVideoId is { } existing)
        {
            job.Status = UploadStatus.Completed;
            job.VideoId = existing;
            return;
        }

        string? videoId = null;
        string? error = null;
        try
        {
            videoId = await _youtubeUpload.UploadAsync(job.Recording, ct);
            if (videoId is null)
                error = "YouTube returned null — revisa los logs del backend.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "Upload failed: {JobId}", job.Id);
        }

        job.VideoId = videoId;
        job.Error = error;
        job.Status = videoId is null ? UploadStatus.Failed : UploadStatus.Completed;
        await SaveSafelyAsync(() => _store.SaveYouTubeAsync(
            job.Recording.FilePath,
            videoId is null ? PipelineStepStatus.Failed : PipelineStepStatus.Done,
            videoId, error, ct), job);

        if (videoId is not null)
            _logger.LogInformation("Upload completed: {JobId} → {VideoId}", job.Id, videoId);
    }

    private async Task SaveSafelyAsync(Func<Task> save, UploadJob job)
    {
        try { await save(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline: no se pudo guardar el estado de {JobId}.", job.Id);
        }
    }
}
