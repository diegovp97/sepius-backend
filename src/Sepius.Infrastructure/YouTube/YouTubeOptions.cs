namespace Sepius.Infrastructure.YouTube;

public sealed class YouTubeOptions
{
    public const string SectionName = "YouTube";

    /// <summary>
    /// Ruta al archivo client_secrets.json descargado desde Google Cloud Console.
    /// </summary>
    public string ClientSecretsPath { get; set; } = "client_secrets.json";

    /// <summary>
    /// Directorio donde se almacena el token OAuth2 entre reinicios.
    /// </summary>
    public string TokenStorePath { get; set; } = "youtube_token";

    /// <summary>
    /// Privacidad del video subido: public, unlisted, private.
    /// </summary>
    public string PrivacyStatus { get; set; } = "unlisted";

    /// <summary>
    /// Si es false, no se sube nada (útil para deshabilitar sin borrar configuración).
    /// </summary>
    public bool Enabled { get; set; } = false;
}
