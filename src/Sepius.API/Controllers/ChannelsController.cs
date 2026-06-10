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
    private readonly IStreamlinkService _streamlink;

    public ChannelsController(IChannelRepository channelRepo, IStreamlinkService streamlink)
    {
        _channelRepo = channelRepo;
        _streamlink = streamlink;
    }

    /// <summary>Devuelve todos los canales registrados con su estado actual.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ChannelResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ChannelResponse>>> GetAll(CancellationToken ct)
    {
        var channels = await _channelRepo.GetAllAsync(ct);
        return Ok(channels.Select(ToResponse));
    }

    /// <summary>Añade un canal a la lista de monitorización.</summary>
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

        return CreatedAtAction(nameof(GetAll), ToResponse(channel));
    }

    /// <summary>Elimina un canal. Si está grabando, detiene la grabación primero.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var channels = await _channelRepo.GetAllAsync(ct);
        var channel = channels.FirstOrDefault(c => c.Id == id);

        if (channel is null)
            return NotFound($"Canal con ID '{id}' no encontrado.");

        if (_streamlink.IsRecording(channel.Name))
            await _streamlink.StopRecordingAsync(channel.Name);

        await _channelRepo.RemoveAsync(id, ct);
        return NoContent();
    }

    // Mapeo de entidad de dominio → DTO de respuesta.
    // El controlador es responsable de esta traducción, no el servicio.
    private ChannelResponse ToResponse(Channel c) => new(
        c.Id,
        c.Name,
        c.IsMonitored,
        c.AddedAt,
        _streamlink.IsRecording(c.Name)
    );
}
