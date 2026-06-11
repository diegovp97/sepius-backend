namespace Sepius.API.Workers;

/// <summary>
/// Opciones de configuración del worker de monitorización.
/// </summary>
public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    /// <summary>Intervalo en segundos entre cada ciclo de verificación de canales.</summary>
    public int PollingIntervalSeconds { get; set; } = 120;

    /// <summary>Canales a registrar automáticamente al arrancar la aplicación.</summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>
    /// Segundos sin keepalive de Twitch antes de considerar la sesión muerta.
    /// Twitch indica el valor en session_welcome; este es el fallback.
    /// </summary>
    public int KeepaliveTimeoutSeconds { get; set; } = 15;
}
