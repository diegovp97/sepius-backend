namespace Sepius.Application.Interfaces;

/// <summary>
/// Gestiona el pipeline streamlink → ffmpeg → HLS.
/// El backend produce los ficheros .m3u8/.ts que Angular reproduce
/// con hls.js, sin ninguna sesión de Twitch del usuario.
/// </summary>
public interface ILiveTranscodeService
{
    bool IsTranscoding(string channelName);

    /// <summary>True cuando ya existen segmentos HLS listos para reproducir.</summary>
    bool IsHlsReady(string channelName);

    /// <summary>URL relativa al backend para que hls.js cargue el manifest.</summary>
    string GetHlsUrl(string channelName);

    Task StartAsync(string channelName, CancellationToken ct = default);
    Task StopAsync(string channelName);
}
