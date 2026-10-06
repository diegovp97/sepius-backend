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

    /// <summary>
    /// Reconstruye una grabación ya terminada a partir de su fichero (subidas manuales y recuperación
    /// tras un reinicio). La hora de inicio sale del nombre (<c>yyyyMMdd_HHmmss</c>, UTC); sin ella,
    /// de la fecha de modificación. Así el título de YouTube refleja cuándo fue el directo.
    /// </summary>
    public static Recording FromFile(string channelName, string filePath)
    {
        var info = new FileInfo(filePath);
        var started = DateTime.TryParseExact(
            Path.GetFileNameWithoutExtension(filePath), "yyyyMMdd_HHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : info.LastWriteTimeUtc;

        return new Recording
        {
            Id = Guid.NewGuid(),
            ChannelName = channelName,
            FilePath = filePath,
            StartedAt = started,
            EndedAt = info.LastWriteTimeUtc,
            Status = RecordingStatus.Completed,
            FileSizeBytes = info.Length
        };
    }

    public static Recording Create(string channelName, string filePath) => new()
    {
        Id = Guid.NewGuid(),
        ChannelName = channelName,
        FilePath = filePath,
        StartedAt = DateTime.UtcNow,
        Status = RecordingStatus.Recording
    };
}
