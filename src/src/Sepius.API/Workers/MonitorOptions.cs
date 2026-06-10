namespace Sepius.API.Workers;

/// <summary>
/// Opciones de configuración del worker de monitorización.
/// </summary>
public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    /// <summary>Intervalo en segundos entre cada ciclo de verificación de canales.</summary>
    public int PollingIntervalSeconds { get; set; } = 120;
}
