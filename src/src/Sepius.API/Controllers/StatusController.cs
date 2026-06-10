using Microsoft.AspNetCore.Mvc;
using Sepius.Application.Interfaces;

namespace Sepius.API.Controllers;

/// <summary>
/// Endpoint de estado general del sistema. Útil para el dashboard de Angular.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class StatusController : ControllerBase
{
    private readonly IChannelRepository _channelRepo;
    private readonly IStreamlinkService _streamlink;

    public StatusController(IChannelRepository channelRepo, IStreamlinkService streamlink)
    {
        _channelRepo = channelRepo;
        _streamlink = streamlink;
    }

    /// <summary>Devuelve un resumen del estado actual del sistema.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var channels = await _channelRepo.GetAllAsync(ct);
        var active = _streamlink.GetActiveRecordings();

        return Ok(new
        {
            Status = "running",
            Timestamp = DateTime.UtcNow,
            MonitoredChannels = channels.Count(c => c.IsMonitored),
            ActiveRecordings = active.Count,
            ActiveChannels = active.Select(r => r.ChannelName)
        });
    }
}
