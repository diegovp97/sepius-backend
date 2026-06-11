using Microsoft.AspNetCore.Mvc;
using Sepius.Application.Interfaces;

namespace Sepius.API.Controllers;

/// <summary>
/// Controla el pipeline de transcoding en vivo (streamlink → ffmpeg → HLS).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class LiveController : ControllerBase
{
    private readonly ILiveTranscodeService _live;

    public LiveController(ILiveTranscodeService live) => _live = live;

    /// <summary>
    /// Inicia el transcode HLS para el canal.
    /// Devuelve la URL del manifest que hls.js debe cargar.
    /// </summary>
    [HttpPost("{channelName}/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Start(string channelName, [FromQuery] string platform = "twitch", CancellationToken ct = default)
    {
        await _live.StartAsync(channelName, platform, ct);
        return Ok(new
        {
            hlsUrl = _live.GetHlsUrl(channelName, platform),
            message = "Transcode iniciado. El stream estará listo en unos segundos."
        });
    }

    /// <summary>
    /// Detiene el transcode y libera los procesos.
    /// </summary>
    [HttpPost("{channelName}/stop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Stop(string channelName, [FromQuery] string platform = "twitch")
    {
        await _live.StopAsync(channelName, platform);
        return NoContent();
    }

    /// <summary>
    /// Estado del transcode: si está activo y si los segmentos HLS ya están listos.
    /// El frontend hace polling a este endpoint hasta que isReady == true.
    /// </summary>
    [HttpGet("{channelName}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Status(string channelName, [FromQuery] string platform = "twitch") =>
        Ok(new
        {
            isTranscoding = _live.IsTranscoding(channelName, platform),
            isReady = _live.IsHlsReady(channelName, platform),
            hlsUrl = _live.GetHlsUrl(channelName, platform)
        });
}
