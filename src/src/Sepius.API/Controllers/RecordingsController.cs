using Microsoft.AspNetCore.Mvc;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.API.Controllers;

/// <summary>
/// Consulta grabaciones activas y completadas, y permite detenerlas.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class RecordingsController : ControllerBase
{
    private readonly IStreamlinkService _streamlink;

    public RecordingsController(IStreamlinkService streamlink)
        => _streamlink = streamlink;

    /// <summary>Devuelve las grabaciones en curso.</summary>
    [HttpGet("active")]
    [ProducesResponseType(typeof(IEnumerable<RecordingResponse>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<RecordingResponse>> GetActive()
        => Ok(_streamlink.GetActiveRecordings().Select(ToResponse));

    /// <summary>Devuelve el historial de grabaciones finalizadas.</summary>
    [HttpGet("completed")]
    [ProducesResponseType(typeof(IEnumerable<RecordingResponse>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<RecordingResponse>> GetCompleted()
        => Ok(_streamlink.GetCompletedRecordings().Select(ToResponse));

    /// <summary>Detiene manualmente la grabación de un canal.</summary>
    [HttpDelete("{channelName}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stop(string channelName)
    {
        if (!_streamlink.IsRecording(channelName))
            return NotFound($"No hay grabación activa para '{channelName}'.");

        await _streamlink.StopRecordingAsync(channelName);
        return NoContent();
    }

    private static RecordingResponse ToResponse(Recording r) => new(
        r.Id,
        r.ChannelName,
        r.FileName,
        r.StartedAt,
        r.EndedAt,
        r.Status,
        r.FileSizeBytes
    );
}
