using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.API.Controllers;

/// <summary>
/// Gestiona los canales de Twitch a monitorizar.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class ChannelsController : ControllerBase
{
    private readonly IChannelRepository _channelRepo;
    private readonly IRecorderClient _recorder;

    public ChannelsController(IChannelRepository channelRepo, IRecorderClient recorder)
    {
        _channelRepo = channelRepo;
        _recorder = recorder;
    }

    /// <summary>Devuelve todos los canales registrados con su estado actual.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ChannelResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ChannelResponse>>> GetAll(CancellationToken ct)
    {
        var channels = await _channelRepo.GetAllAsync(ct);
        var recording = await RecordingChannelsAsync(ct);
        return Ok(channels.Select(c => ToResponse(c, recording.Contains(c.Name))));
    }

    /// <summary>Añade un canal a la lista de monitorización.</summary>
    [Authorize]
    [HttpPost]
    [ProducesResponseType(typeof(ChannelResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ChannelResponse>> Add(
        [FromBody] AddChannelRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("El nombre del canal no puede estar vacío.");

        var existing = await _channelRepo.GetByNameAsync(request.Name, ct);
        if (existing is not null)
            return Conflict($"El canal '{request.Name}' ya está registrado.");

        var channel = Channel.Create(request.Name);
        await _channelRepo.AddAsync(channel, ct);

        return CreatedAtAction(nameof(GetAll), ToResponse(channel, isRecording: false));
    }

    /// <summary>Elimina un canal. Si está grabando, detiene la grabación primero.</summary>
    [Authorize]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var channels = await _channelRepo.GetAllAsync(ct);
        var channel = channels.FirstOrDefault(c => c.Id == id);

        if (channel is null)
            return NotFound($"Canal con ID '{id}' no encontrado.");

        // Si está grabando, se detiene primero (en el grabador). Si el grabador no responde, se borra igualmente.
        try
        {
            var sessions = await _recorder.GetSessionsAsync(ct);
            foreach (var s in sessions.Where(s => s.Channel.Equals(channel.Name, StringComparison.OrdinalIgnoreCase)))
                await _recorder.StopAsync(s.Channel, s.Platform, ct);
        }
        catch (HttpRequestException) { }

        await _channelRepo.RemoveAsync(id, ct);
        return NoContent();
    }

    // Mapeo de entidad de dominio → DTO de respuesta.
    // El controlador es responsable de esta traducción, no el servicio.
    private static ChannelResponse ToResponse(Channel c, bool isRecording) => new(
        c.Id,
        c.Name,
        c.IsMonitored,
        c.AddedAt,
        isRecording
    );

    /// <summary>Nombres de los canales que el grabador está grabando ahora mismo.</summary>
    private async Task<HashSet<string>> RecordingChannelsAsync(CancellationToken ct)
    {
        var sessions = await _recorder.GetSessionsAsync(ct);
        return new HashSet<string>(sessions.Select(s => s.Channel), StringComparer.OrdinalIgnoreCase);
    }
}
