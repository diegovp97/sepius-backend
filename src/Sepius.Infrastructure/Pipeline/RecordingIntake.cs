using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.Pipeline;

public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>
    /// Duración mínima para subir una grabación sola a Drive/YouTube. Evita subir el clip de anuncio de Twitch
    /// (~16 s) o arranques fallidos. 0 = subir todo.
    /// </summary>
    public int MinDurationMinutes { get; set; } = 5;
}

/// <summary>
/// Lo que hace el grabador cuando termina una grabación: aplica el filtro de duración y la apunta en
/// <c>upload_pipeline</c>. La API (otro proceso) la recoge de ahí y la sube, así que el grabador no
/// necesita saber nada de Drive ni de YouTube.
/// </summary>
public sealed class RecordingIntake(
    UploadPipelineStore store,
    IOptions<PipelineOptions> options,
    ILogger<RecordingIntake> logger)
{
    public async Task<bool> HandleAsync(Recording recording, CancellationToken ct = default)
    {
        var min = TimeSpan.FromMinutes(Math.Max(0, options.Value.MinDurationMinutes));
        if (recording.Duration < min)
        {
            logger.LogWarning(
                "Grabación de '{Channel}' demasiado corta ({Seconds:F0} s < {Min} min): no se sube. Fichero: {File}",
                recording.ChannelName, recording.Duration.TotalSeconds, min.TotalMinutes, recording.FileName);
            return false;
        }

        await store.GetOrCreateAsync(recording, resetYouTube: false, ct);
        logger.LogInformation(
            "Grabación completada para '{Channel}' ({File}, {Minutes:F0} min): apuntada para subir (Drive → YouTube).",
            recording.ChannelName, recording.FileName, recording.Duration.TotalMinutes);
        return true;
    }
}
