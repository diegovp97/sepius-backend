namespace Sepius.Infrastructure.Recorder;

/// <summary>
/// Conexión API ↔ grabador. La misma sección (Recorder__*) la leen los dos lados:
/// la API para llamar y el grabador para validar la clave.
/// </summary>
public sealed class RecorderOptions
{
    public const string SectionName = "Recorder";
    public const string KeyHeader = "X-Recorder-Key";

    /// <summary>Dirección interna del grabador en la red de Docker (no está publicada fuera).</summary>
    public string BaseUrl { get; set; } = "http://sepius-recorder:9090";

    /// <summary>Clave compartida. Obligatoria (mínimo 16 caracteres): sin ella la API del grabador rechaza todo.</summary>
    public string Key { get; set; } = string.Empty;
}
