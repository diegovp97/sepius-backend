using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.Persistence;

namespace Sepius.Infrastructure.Pipeline;

/// <summary>
/// Guarda en Postgres qué pasos del pipeline (Drive → YouTube) ha completado cada grabación,
/// para retomar tras un reinicio y para poder reintentar solo lo que falló.
/// Es singleton: abre un ámbito (scope) por operación porque AppDbContext es scoped.
/// </summary>
public sealed class UploadPipelineStore(IServiceScopeFactory scopes)
{
    /// <summary>Devuelve la fila de la grabación; la crea si no existe. Con <paramref name="resetYouTube"/> reabre solo el paso de YouTube.</summary>
    public async Task<UploadPipeline> GetOrCreateAsync(Recording rec, bool resetYouTube, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.UploadPipelines.FirstOrDefaultAsync(x => x.FilePath == rec.FilePath, ct);
        if (row is null)
        {
            row = new UploadPipeline
            {
                FilePath = rec.FilePath,
                ChannelName = rec.ChannelName,
                FileSizeBytes = rec.FileSizeBytes
            };
            db.UploadPipelines.Add(row);
        }
        else if (resetYouTube)
        {
            row.YouTubeStatus = PipelineStepStatus.Pending;
            row.YouTubeVideoId = null;
            row.YouTubeError = null;
            row.YouTubeAttempts = 0;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return row;
    }

    public Task SaveDriveAsync(string filePath, PipelineStepStatus status, string? fileId, string? error, CancellationToken ct = default)
        => UpdateAsync(filePath, ct,
            row =>
            {
                row.DriveStatus = status;
                row.DriveFileId = fileId ?? row.DriveFileId;
                row.DriveError = error;
                if (status == PipelineStepStatus.Failed) row.DriveAttempts++;
            });

    public Task SaveYouTubeAsync(string filePath, PipelineStepStatus status, string? videoId, string? error, CancellationToken ct = default)
        => UpdateAsync(filePath, ct,
            row =>
            {
                row.YouTubeStatus = status;
                row.YouTubeVideoId = videoId ?? row.YouTubeVideoId;
                row.YouTubeError = error;
                if (status == PipelineStepStatus.Failed) row.YouTubeAttempts++;
            });

    private async Task UpdateAsync(string filePath, CancellationToken ct, Action<UploadPipeline> apply)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.UploadPipelines.FirstOrDefaultAsync(x => x.FilePath == filePath, ct);
        if (row is null) return;
        apply(row);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Filas con algún paso (habilitado) sin completar y con intentos disponibles, no tocadas desde <paramref name="idleSince"/>.</summary>
    public async Task<List<UploadPipeline>> ListIncompleteAsync(bool driveEnabled, bool youTubeEnabled, int maxAttempts, DateTime idleSince, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.UploadPipelines.AsNoTracking()
            .Where(x => x.UpdatedAt <= idleSince)
            .Where(x =>
                (driveEnabled && x.DriveStatus != PipelineStepStatus.Done && x.DriveAttempts < maxAttempts) ||
                (youTubeEnabled && x.YouTubeStatus != PipelineStepStatus.Done && x.YouTubeAttempts < maxAttempts))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<UploadPipeline>> ListRecentAsync(int take, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.UploadPipelines.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }
}
