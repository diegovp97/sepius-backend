using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sepius.Application.Interfaces;

namespace Sepius.API.Controllers;

/// <summary>
/// Estado y control del directo. El pipeline (streamlink → ffmpeg → HLS) vive en el grabador
/// (sepius-recorder); esta API solo le pregunta, así que reiniciarla no corta ninguna grabación.
/// Los ficheros HLS se sirven desde el volumen compartido en /live/...
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class LiveController : ControllerBase
{
    private readonly IRecorderClient _recorder;
    private readonly ILogger<LiveController> _logger;

    public LiveController(IRecorderClient recorder, ILogger<LiveController> logger)
    {
        _recorder = recorder;
        _logger   = logger;
    }

    [Authorize]
    [HttpPost("{channelName}/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Start(string channelName, [FromQuery] string platform = "twitch", CancellationToken ct = default)
    {
        _logger.LogInformation("[Live] POST /start → canal='{Channel}' platform='{Platform}'", channelName, platform);
        try
        {
            var hlsUrl = await _recorder.StartAsync(channelName, platform, ct);
            return Ok(new
            {
                hlsUrl,
                message = "Transcode iniciado. El stream estará listo en unos segundos."
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("[Live] El grabador no pudo arrancar '{Channel}': {Reason}", channelName, ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "El grabador no responde." });
        }
    }

    [Authorize]
    [HttpPost("{channelName}/stop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Stop(string channelName, [FromQuery] string platform = "twitch", CancellationToken ct = default)
    {
        _logger.LogInformation("[Live] POST /stop → canal='{Channel}' platform='{Platform}'", channelName, platform);
        try
        {
            await _recorder.StopAsync(channelName, platform, ct);
            return NoContent();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("[Live] El grabador no pudo parar '{Channel}': {Reason}", channelName, ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "El grabador no responde." });
        }
    }

    [HttpGet("{channelName}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(string channelName, [FromQuery] string platform = "twitch", CancellationToken ct = default)
        => Ok(await _recorder.GetStatusAsync(channelName, platform, ct));

    [HttpGet("{channelName}/active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Active(string channelName, CancellationToken ct = default)
        => Ok(await _recorder.GetActiveAsync(channelName, ct));
}
