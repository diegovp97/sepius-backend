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
    public string PrivacyStatus { get; set; } = "unlisted";

    /// <summary>
    /// Si es false, no se sube nada.
    /// </summary>
    public bool Enabled { get; set; } = false;
}
