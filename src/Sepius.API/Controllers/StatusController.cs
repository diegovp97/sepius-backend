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
    private readonly IRecorderClient _recorder;

    public StatusController(IChannelRepository channelRepo, IRecorderClient recorder)
    {
        _channelRepo = channelRepo;
        _recorder = recorder;
    }

    /// <summary>Devuelve un resumen del estado actual del sistema.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var channels = await _channelRepo.GetAllAsync(ct);
        var sessions = await _recorder.GetSessionsAsync(ct);

        return Ok(new
        {
            Status = "running",
            Timestamp = DateTime.UtcNow,
            MonitoredChannels = channels.Count(c => c.IsMonitored),
            ActiveRecordings = sessions.Count,
            ActiveChannels = sessions.Select(s => s.Channel)
        });
    }
}
