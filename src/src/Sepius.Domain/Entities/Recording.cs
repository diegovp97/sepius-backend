namespace Sepius.Domain.Entities;

public enum RecordingStatus
{
    Recording,
    Completed,
    Failed
}

/// <summary>
/// Representa una sesión de grabación de un stream.
/// Usa <c>init</c> setters (C# 9+): las propiedades solo se asignan durante
/// la inicialización del objeto, haciéndolo inmutable tras la construcción
/// (excepto las marcadas con <c>set</c> para actualizaciones de estado).
/// </summary>
public sealed class Recording
{
    public Guid Id { get; init; }
    public string ChannelName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }

    // Estas propiedades se actualizan cuando el proceso termina
    public DateTime? EndedAt { get; set; }
    public RecordingStatus Status { get; set; }
    public long FileSizeBytes { get; set; }

    // Propiedad calculada: no se almacena, se deriva de FilePath
    public string FileName => Path.GetFileName(FilePath);

    public static Recording Create(string channelName, string filePath) => new()
    {
        Id = Guid.NewGuid(),
        ChannelName = channelName,
        FilePath = filePath,
        StartedAt = DateTime.UtcNow,
        Status = RecordingStatus.Recording
    };
}
