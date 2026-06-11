namespace Sepius.Application.Interfaces;

/// <summary>
/// Gestiona el pipeline streamlink → ffmpeg → HLS.
/// El backend produce los ficheros .m3u8/.ts que Angular reproduce
/// con hls.js, sin ninguna sesión de Twitch del usuario.
/// </summary>
public interface ILiveTranscodeService
{
    bool IsTranscoding(string channelName, string platform = "twitch");

    /// <summary>True cuando ya existen segmentos HLS listos para reproducir.</summary>
    bool IsHlsReady(string channelName, string platform = "twitch");

    /// <summary>URL relativa al backend para que hls.js cargue el manifest.</summary>
    string GetHlsUrl(string channelName, string platform = "twitch");

    Task StartAsync(string channelName, string platform = "twitch", CancellationToken ct = default);
    Task StopAsync(string channelName, string platform = "twitch");
}
