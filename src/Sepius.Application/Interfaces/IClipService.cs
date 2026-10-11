using Sepius.Application.DTOs;

namespace Sepius.Application.Interfaces;

/// <summary>Corta clips a partir de los segmentos HLS del directo. Vive en el grabador (tiene ffmpeg y el HLS).</summary>
public interface IClipService
{
    /// <returns>El clip, o null si no hay directo o aún no hay vídeo suficiente.</returns>
    Task<ClipCut?> CutAsync(string channel, string platform, int seconds, int behindSeconds, CancellationToken ct = default);
}

/// <summary>Cliente de la API (otro contenedor) para pedirle un clip al grabador. Lanza HttpRequestException si falla.</summary>
public interface IClipRecorder
{
    Task<ClipCut?> CreateAsync(string channel, string platform, int seconds, int behindSeconds, CancellationToken ct = default);
}
