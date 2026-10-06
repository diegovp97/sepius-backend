using Sepius.Application.DTOs;

namespace Sepius.Application.Interfaces;

/// <summary>
/// Cliente de la API interna del grabador (contenedor sepius-recorder).
/// La API ya no ejecuta ffmpeg/Streamlink: le pregunta al grabador, de modo que
/// reiniciar la API no interrumpe ninguna grabación.
/// Si el grabador no responde, los métodos de consulta devuelven "sin directo" en vez de lanzar.
/// </summary>
public interface IRecorderClient
{
    Task<LiveActiveDto> GetActiveAsync(string channel, CancellationToken ct = default);
    Task<LiveStatusDto> GetStatusAsync(string channel, string platform, CancellationToken ct = default);
    Task<string?> StartAsync(string channel, string platform, CancellationToken ct = default);
    Task StopAsync(string channel, string platform, CancellationToken ct = default);
    Task<IReadOnlyList<LiveSessionDto>> GetSessionsAsync(CancellationToken ct = default);
}
