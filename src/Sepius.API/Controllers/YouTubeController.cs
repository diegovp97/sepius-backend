using Microsoft.AspNetCore.Mvc;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;

namespace Sepius.API.Controllers;

/// <summary>
/// Gestión de videos en YouTube (listar, borrar).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class YouTubeController : ControllerBase
{
    private readonly IYouTubeUploadService _youtube;
    private readonly ILogger<YouTubeController> _logger;

    public YouTubeController(IYouTubeUploadService youtube, ILogger<YouTubeController> logger)
    {
        _youtube = youtube;
        _logger = logger;
    }

    /// <summary>Lista los videos subidos al canal de YouTube.</summary>
    [HttpGet("videos")]
    [ProducesResponseType(typeof(IEnumerable<YouTubeVideoDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVideos(CancellationToken ct)
    {
        var videos = await _youtube.GetMyVideosAsync(ct);
        return Ok(videos);
    }

    /// <summary>Borra un video de YouTube por su ID.</summary>
    [HttpDelete("videos/{videoId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteVideo(string videoId, CancellationToken ct)
    {
        _logger.LogInformation("Solicitud de borrado de video YouTube: {VideoId}", videoId);
        var deleted = await _youtube.DeleteVideoAsync(videoId, ct);

        if (!deleted)
            return StatusCode(500, $"No se pudo borrar el video {videoId}. Verifica los permisos del token OAuth.");

        return NoContent();
    }
}
