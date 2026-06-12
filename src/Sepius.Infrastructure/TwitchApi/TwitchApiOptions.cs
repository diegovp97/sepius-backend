namespace Sepius.Infrastructure.TwitchApi;

public sealed class TwitchApiOptions
{
    public const string SectionName = "TwitchApi";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = "https://id.twitch.tv/oauth2/token";
    public string ApiBaseUrl { get; set; } = "https://api.twitch.tv/helix";
}
