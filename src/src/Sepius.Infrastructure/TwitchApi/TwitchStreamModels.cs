using System.Text.Json.Serialization;

namespace Sepius.Infrastructure.TwitchApi;

// Modelos internos para deserializar las respuestas de la API de Twitch.
// Se marcan como internal porque son un detalle de implementación.
// [JsonPropertyName] mapea el nombre snake_case del JSON al PascalCase de C#.
// Equivale a usar { access_token: string } en una interface TypeScript.

internal sealed class TwitchTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

internal sealed class TwitchStreamsResponse
{
    [JsonPropertyName("data")]
    public List<TwitchStreamData> Data { get; set; } = [];
}

internal sealed class TwitchStreamData
{
    [JsonPropertyName("user_login")]
    public string UserLogin { get; set; } = string.Empty;

    /// <summary>"live" cuando el canal está en directo, string vacío si no lo está.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("viewer_count")]
    public int ViewerCount { get; set; }
}
