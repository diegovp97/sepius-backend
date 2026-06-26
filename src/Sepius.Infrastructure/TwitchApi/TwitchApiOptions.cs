namespace Sepius.Infrastructure.TwitchApi;

public sealed class TwitchApiOptions
{
    public const string SectionName = "TwitchApi";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = "https://id.twitch.tv/oauth2/token";
    public string ApiBaseUrl { get; set; } = "https://api.twitch.tv/helix";

    /// <summary>
    /// User access token for EventSub WebSocket subscriptions.
    /// stream.online/stream.offline require a user token, not an app token.
    /// Obtained via OAuth2 Authorization Code Flow.
    /// </summary>
    public string UserAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Refresh token used to obtain new user access tokens via Authorization Code Flow.
    /// When set, the service will automatically refresh the user access token before expiry.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Redirect URI configured in the Twitch Developer Console for the Authorization Code Flow.
    /// Must match exactly (including trailing slash).
    /// </summary>
    public string RedirectUri { get; set; } = "http://localhost";
}
