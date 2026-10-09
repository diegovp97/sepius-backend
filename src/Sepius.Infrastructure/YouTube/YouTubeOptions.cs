namespace Sepius.Infrastructure.YouTube;

public sealed class YouTubeOptions
{
    public const string SectionName = "YouTube";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>
    /// Privacidad del video subido: public, unlisted, private.
    /// </summary>
    public string PrivacyStatus { get; set; } = "public";

    /// <summary>
    /// Si es false, no se sube nada.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Borra el MP4 local cuando ya está en YouTube y, si el respaldo de Drive está activo, también en Drive.
    /// Nunca borra mientras algún paso esté pendiente o haya fallado.
    /// </summary>
    public bool DeleteAfterUpload { get; set; } = false;
}
