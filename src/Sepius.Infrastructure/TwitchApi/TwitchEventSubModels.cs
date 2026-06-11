using System.Text.Json.Serialization;

namespace Sepius.Infrastructure.TwitchApi;

// ── MENSAJES QUE ENVÍA TWITCH POR EL WEBSOCKET ───────────────────────────────
// Twitch envía siempre un envelope con "metadata" y "payload".
// El tipo del mensaje está en metadata.message_type.

public sealed record EventSubEnvelope
{
    [JsonPropertyName("metadata")]
    public EventSubMetadata Metadata { get; init; } = new();

    [JsonPropertyName("payload")]
    public EventSubPayload Payload { get; init; } = new();
}

public sealed record EventSubMetadata
{
    [JsonPropertyName("message_id")]
    public string MessageId { get; init; } = string.Empty;

    [JsonPropertyName("message_type")]
    public string MessageType { get; init; } = string.Empty;

    [JsonPropertyName("message_timestamp")]
    public DateTimeOffset MessageTimestamp { get; init; }

    [JsonPropertyName("subscription_type")]
    public string? SubscriptionType { get; init; }
}

public sealed record EventSubPayload
{
    [JsonPropertyName("session")]
    public EventSubSession? Session { get; init; }

    [JsonPropertyName("subscription")]
    public EventSubSubscription? Subscription { get; init; }

    [JsonPropertyName("event")]
    public EventSubEvent? Event { get; init; }
}

public sealed record EventSubSession
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("keepalive_timeout_seconds")]
    public int? KeepaliveTimeoutSeconds { get; init; }

    [JsonPropertyName("reconnect_url")]
    public string? ReconnectUrl { get; init; }
}

public sealed record EventSubSubscription
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

// El evento stream.online y stream.offline tienen broadcaster_user_login
public sealed record EventSubEvent
{
    [JsonPropertyName("broadcaster_user_login")]
    public string BroadcasterUserLogin { get; init; } = string.Empty;

    [JsonPropertyName("broadcaster_user_id")]
    public string BroadcasterUserId { get; init; } = string.Empty;
}

// ── REQUEST PARA CREAR UNA SUSCRIPCIÓN ───────────────────────────────────────
// POST /helix/eventsub/subscriptions

public sealed record EventSubSubscriptionRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1";

    [JsonPropertyName("condition")]
    public EventSubCondition Condition { get; init; } = new();

    [JsonPropertyName("transport")]
    public EventSubTransport Transport { get; init; } = new();
}

public sealed record EventSubCondition
{
    [JsonPropertyName("broadcaster_user_id")]
    public string BroadcasterUserId { get; init; } = string.Empty;
}

public sealed record EventSubTransport
{
    [JsonPropertyName("method")]
    public string Method { get; init; } = "websocket";

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;
}

// ── RESPUESTA DEL ENDPOINT DE USUARIOS ───────────────────────────────────────
// Necesario para obtener el broadcaster_user_id a partir del login name

public sealed record TwitchUsersResponse
{
    [JsonPropertyName("data")]
    public List<TwitchUser> Data { get; init; } = [];
}

public sealed record TwitchUser
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("login")]
    public string Login { get; init; } = string.Empty;
}
