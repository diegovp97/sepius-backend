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
    private readonly IYouTubeUploadService _youtubeUpload;
    private readonly ILogger<RecordingsController> _logger;

    public RecordingsController(
        IStreamlinkService streamlink,
        IYouTubeUploadService youtubeUpload,
        ILogger<RecordingsController> logger)
    {
        _streamlink = streamlink;
        _youtubeUpload = youtubeUpload;
        _logger = logger;
    }

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

    /// <summary>Sube una grabación existente a YouTube por su ruta de archivo.</summary>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Upload([FromQuery] string filePath, [FromQuery] string channelName = "elttblue")
    {
        if (!System.IO.File.Exists(filePath))
            return NotFound($"Archivo no encontrado: {filePath}");

        var fileInfo = new FileInfo(filePath);
        var recording = Recording.Create(channelName, filePath);
        recording.Status = RecordingStatus.Completed;
        recording.EndedAt = fileInfo.LastWriteTimeUtc;
        recording.FileSizeBytes = fileInfo.Length;

        _logger.LogInformation("Upload manual solicitado para '{File}'", filePath);
        var videoId = await _youtubeUpload.UploadAsync(recording);

        if (videoId is null)
            return StatusCode(500, "Error al subir a YouTube. Revisa los logs.");

        return Ok(new { videoId, url = $"https://youtu.be/{videoId}" });
    }

    /// <summary>Lista las grabaciones MP4 en el directorio de recordings.</summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public IActionResult List([FromQuery] string channelName = "elttblue")
    {
        var recordingsPath = $"/recordings/twitch/{channelName}";
        if (!Directory.Exists(recordingsPath))
            return NotFound($"Directorio no encontrado: {recordingsPath}");

        var files = Directory.GetFiles(recordingsPath, "*.mp4")
            .OrderByDescending(f => f)
            .Select(f => new FileInfo(f))
            .Select(fi => new
            {
                fileName = fi.Name,
                fullPath = fi.FullName,
                sizeMB = Math.Round(fi.Length / 1_048_576.0, 1),
                lastModified = fi.LastWriteTimeUtc
            });

        return Ok(files);
    }
}
