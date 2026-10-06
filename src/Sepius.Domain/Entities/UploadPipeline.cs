namespace Sepius.Domain.Entities;

public enum PipelineStepStatus
{
    /// <summary>Aún no se ha intentado (o se interrumpió a medias).</summary>
    Pending,
    Done,
    Failed
}

/// <summary>
/// Estado persistente del pipeline de una grabación:
/// grabación → Google Drive (respaldo) → YouTube.
/// Se guarda en Postgres para poder retomar tras un reinicio o un fallo.
/// </summary>
public sealed class UploadPipeline
{
    /// <summary>Ruta del .mp4 en la VPS. Es la clave: un fichero = una fila.</summary>
    public string FilePath { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public PipelineStepStatus DriveStatus { get; set; } = PipelineStepStatus.Pending;
    public string? DriveFileId { get; set; }
    public string? DriveError { get; set; }
    public int DriveAttempts { get; set; }

    public PipelineStepStatus YouTubeStatus { get; set; } = PipelineStepStatus.Pending;
    public string? YouTubeVideoId { get; set; }
    public string? YouTubeError { get; set; }
    public int YouTubeAttempts { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
