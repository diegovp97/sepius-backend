using Sepius.Domain.Entities;

namespace Sepius.Application.Interfaces;

/// <summary>
/// Contrato para gestionar el ciclo de vida de los procesos de Streamlink.
/// Se registrará como Singleton porque los procesos del SO deben sobrevivir
/// entre peticiones HTTP.
/// </summary>
public interface IStreamlinkService
{
    bool IsRecording(string channelName);
    Task StartRecordingAsync(string channelName, CancellationToken ct = default);
    Task StopRecordingAsync(string channelName);
    IReadOnlyList<Recording> GetActiveRecordings();
    IReadOnlyList<Recording> GetCompletedRecordings();

    /// <summary>
    /// Se dispara cuando una grabación termina (ya sea por StopRecordingAsync
    /// o porque el proceso de Streamlink terminó de forma natural).
    /// El handler recibe la grabación completada y puede usarla para post-procesado
    /// (ej: subir a YouTube).
    /// </summary>
    event Func<Recording, Task>? RecordingCompleted;
}
