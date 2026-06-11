namespace Sepius.Application.Interfaces;

/// <summary>
/// Contrato para consultar el estado de un canal en la API de Twitch.
/// La implementación real en Infrastructure usará HTTP; en tests se puede mockear.
/// </summary>
public interface ITwitchApiService
{
    /// <summary>
    /// Devuelve true si el canal está actualmente en directo.
    /// </summary>
    Task<bool> IsChannelLiveAsync(string channelName, CancellationToken ct = default);

    /// <summary>
    /// Obtiene el user_id de Twitch a partir del login name del canal.
    /// </summary>
    Task<string?> GetUserIdAsync(string channelName, CancellationToken ct = default);

    /// <summary>
    /// Crea una suscripción EventSub (stream.online o stream.offline) vinculada
    /// a una sesión WebSocket activa.
    /// </summary>
    Task SubscribeToStreamEventsAsync(
        string sessionId,
        string broadcasterId,
        string eventType,
        CancellationToken ct = default);
}
