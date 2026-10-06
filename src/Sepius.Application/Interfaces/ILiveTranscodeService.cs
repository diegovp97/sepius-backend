using Sepius.Application.DTOs;
using Sepius.Domain.Entities;

namespace Sepius.Application.Interfaces;

/// <summary>
/// Gestiona el pipeline streamlink → ffmpeg → HLS + grabación MP4 integrada.
/// Un solo proceso de streamlink por canal (ahorro de CPU y bandwidth).
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

    /// <summary>Sesiones de grabación en curso (arrancando o grabando).</summary>
    IReadOnlyList<LiveSessionDto> GetActiveSessions();

    /// <summary>
    /// Para todas las sesiones con cierre limpio (SIGINT a ffmpeg, MP4 finalizado) y espera hasta
    /// <paramref name="timeout"/> a que terminen. Se usa al apagar el grabador.
    /// </summary>
    Task StopAllAsync(TimeSpan timeout);

    /// <summary>
    /// Se dispara cuando la grabación MP4 integrada termina (post-stream).
    /// El handler puede subir el .mp4 a YouTube u otro destino.
    /// </summary>
    event Func<Recording, Task>? RecordingCompleted;
}
