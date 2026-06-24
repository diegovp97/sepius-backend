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
    private readonly ILogger<LiveController> _logger;

    public LiveController(ILiveTranscodeService live, ILogger<LiveController> logger)
    {
        _live   = live;
        _logger = logger;
    }

    [HttpPost("{channelName}/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Start(string channelName, [FromQuery] string platform = "twitch", CancellationToken ct = default)
    {
        _logger.LogInformation("[Live] POST /start \u2192 canal='{Channel}' platform='{Platform}'", channelName, platform);
        await _live.StartAsync(channelName, platform, ct);
        var hlsUrl = _live.GetHlsUrl(channelName, platform);
        _logger.LogInformation("[Live] Transcode arrancado. hlsUrl='{Url}'", hlsUrl);
        return Ok(new
        {
            hlsUrl,
            message = "Transcode iniciado. El stream estará listo en unos segundos."
        });
    }

    [HttpPost("{channelName}/stop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Stop(string channelName, [FromQuery] string platform = "twitch")
    {
        _logger.LogInformation("[Live] POST /stop \u2192 canal='{Channel}' platform='{Platform}'", channelName, platform);
        await _live.StopAsync(channelName, platform);
        return NoContent();
    }

    [HttpGet("{channelName}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Status(string channelName, [FromQuery] string platform = "twitch")
    {
        var isTranscoding = _live.IsTranscoding(channelName, platform);
        var isReady       = _live.IsHlsReady(channelName, platform);
        _logger.LogDebug("[Live] GET /status \u2192 canal='{Channel}' platform='{Platform}' transcoding={T} ready={R}",
            channelName, platform, isTranscoding, isReady);
        return Ok(new
        {
            isTranscoding,
            isReady,
            hlsUrl = _live.GetHlsUrl(channelName, platform)
        });
    }

    [HttpGet("{channelName}/active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Active(string channelName)
    {
        string[] platforms = ["kick", "twitch"];

        foreach (var platform in platforms)
        {
            if (_live.IsTranscoding(channelName, platform))
            {
                var hlsUrl  = _live.GetHlsUrl(channelName, platform);
                var isReady = _live.IsHlsReady(channelName, platform);
                _logger.LogInformation(
                    "[Live] GET /active \u2192 canal='{Channel}' LIVE en '{Platform}' | ready={Ready} | url={Url}",
                    channelName, platform, isReady, hlsUrl);
                return Ok(new
                {
                    isLive  = true,
                    platform,
                    channel = channelName,
                    hlsUrl,
                    isReady
                });
            }
        }

        _logger.LogDebug("[Live] GET /active \u2192 canal='{Channel}' offline (ning\u00fan transcode activo).", channelName);
        return Ok(new
        {
            isLive   = false,
            platform = (string?)null,
            channel  = channelName,
            hlsUrl   = (string?)null,
            isReady  = false
        });
    }
}
