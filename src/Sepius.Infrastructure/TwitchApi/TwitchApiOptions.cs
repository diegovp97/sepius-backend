namespace Sepius.Infrastructure.TwitchApi;

/// <summary>
/// PATRÓN IOptions: La configuración se mapea a clases fuertemente tipadas.
/// En Node.js accederías a process.env.TWITCH_CLIENT_ID directamente.
/// Aquí se inyecta IOptions&lt;TwitchApiOptions&gt; en los servicios, lo que permite
/// validar tipos en tiempo de compilación y testear con valores inyectados.
/// </summary>
public sealed class TwitchApiOptions
{
    /// <summary>El nombre de la sección en appsettings.json.</summary>
    public const string SectionName = "TwitchApi";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = "https://id.twitch.tv/oauth2/token";
    public string ApiBaseUrl { get; set; } = "https://api.twitch.tv/helix";
}
