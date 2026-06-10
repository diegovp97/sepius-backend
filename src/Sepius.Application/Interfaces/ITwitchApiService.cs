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
}
