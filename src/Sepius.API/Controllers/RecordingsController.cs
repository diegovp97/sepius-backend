using Microsoft.AspNetCore.Mvc;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.YouTube;

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
    private readonly YouTubeUploadQueue _uploadQueue;
    private readonly ILogger<RecordingsController> _logger;

    public RecordingsController(
        IStreamlinkService streamlink,
        YouTubeUploadQueue uploadQueue,
        ILogger<RecordingsController> logger)
    {
        _streamlink = streamlink;
        _uploadQueue = uploadQueue;
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

    /// <summary>Sube una grabación existente a YouTube por su ruta de archivo (fire-and-forget).</summary>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(object), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Upload([FromQuery] string filePath, [FromQuery] string channelName = "elttblue")
    {
        if (!System.IO.File.Exists(filePath))
            return NotFound($"Archivo no encontrado: {filePath}");

        var fileInfo = new FileInfo(filePath);
        var recording = Recording.Create(channelName, filePath);
        recording.Status = RecordingStatus.Completed;
        recording.EndedAt = fileInfo.LastWriteTimeUtc;
        recording.FileSizeBytes = fileInfo.Length;

        _logger.LogInformation("Upload encolado para '{File}'", filePath);
        var job = _uploadQueue.Enqueue(recording);

        return Accepted(new
        {
            jobId = job.Id,
            message = "Subida encolada. Consulta /api/recordings/upload/status/{jobId} para el progreso."
        });
    }

    /// <summary>Devuelve el estado de una subida encolada.</summary>
    [HttpGet("upload/status/{jobId}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult UploadStatus(string jobId)
    {
        var job = _uploadQueue.GetJob(jobId);
        if (job is null)
            return NotFound($"Job no encontrado: {jobId}");

        return Ok(new
        {
            job.Id,
            status = job.Status.ToString(),
            fileName = job.Recording.FileName,
            videoId = job.VideoId,
            error = job.Error,
            queuedAt = job.QueuedAt,
            startedAt = job.StartedAt,
            completedAt = job.CompletedAt
        });
    }

    /// <summary>Devuelve todos los jobs de subida (activos y recientes).</summary>
    [HttpGet("upload/status")]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public IActionResult UploadStatusAll()
    {
        var jobs = _uploadQueue.GetAllJobs().Select(j => new
        {
            j.Id,
            status = j.Status.ToString(),
            fileName = j.Recording.FileName,
            videoId = j.VideoId,
            error = j.Error,
            queuedAt = j.QueuedAt,
            startedAt = j.StartedAt,
            completedAt = j.CompletedAt
        });
        return Ok(jobs);
    }

    /// <summary>Lista las grabaciones MP4 en el directorio de recordings.</summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public IActionResult List([FromQuery] string channelName = "elttblue", [FromQuery] string platform = "twitch")
    {
        var normalizedPlatform = platform.ToLowerInvariant().Trim() is "kick" ? "kick" : "twitch";
        var recordingsPath = $"/recordings/{normalizedPlatform}/{channelName}";
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

    /// <summary>Sirve un archivo MP4 para streaming/preview/descarga con soporte de range requests.</summary>
    [HttpGet("stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Stream([FromQuery] string filePath, [FromQuery] bool download = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BadRequest("filePath es obligatorio.");

        if (!System.IO.File.Exists(filePath))
            return NotFound($"Archivo no encontrado: {filePath}");

        var fileName = Path.GetFileName(filePath);

        if (download)
        {
            return PhysicalFile(filePath, "video/mp4", fileName, enableRangeProcessing: false);
        }

        Response.Headers["Accept-Ranges"] = "bytes";
        Response.Headers["X-Accel-Buffering"] = "no";

        var fileInfo = new FileInfo(filePath);
        var totalLength = fileInfo.Length;

        if (!Request.Headers.ContainsKey("Range"))
        {
            Response.ContentLength = totalLength;
            Response.ContentType = "video/mp4";
            return File(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read), "video/mp4");
        }

        var rangeHeader = Request.Headers.Range.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)");
        if (!match.Success)
        {
            Response.ContentLength = totalLength;
            Response.ContentType = "video/mp4";
            return File(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read), "video/mp4");
        }

        var start = long.Parse(match.Groups[1].Value);
        var end = match.Groups[2].Success && !string.IsNullOrEmpty(match.Groups[2].Value)
            ? long.Parse(match.Groups[2].Value)
            : totalLength - 1;

        if (start >= totalLength || start > end)
        {
            Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            Response.Headers.ContentRange = $"bytes */{totalLength}";
            return new EmptyResult();
        }

        if (end >= totalLength)
            end = totalLength - 1;

        var chunkSize = end - start + 1;

        Response.StatusCode = StatusCodes.Status206PartialContent;
        Response.ContentType = "video/mp4";
        Response.ContentLength = chunkSize;
        Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";

        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(start, SeekOrigin.Begin);

        var boundedStream = new BoundedStream(stream, chunkSize);
        return new FileStreamResult(boundedStream, "video/mp4");
    }

    /// <summary>Stream wrapper that limits reads to a maximum number of bytes.</summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _read;

        public BoundedStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= _maxBytes) return 0;
            var remaining = (int)Math.Min(count, _maxBytes - _read);
            var n = _inner.Read(buffer, offset, remaining);
            _read += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_read >= _maxBytes) return 0;
            var remaining = (int)Math.Min(count, _maxBytes - _read);
            var n = await _inner.ReadAsync(buffer, offset, remaining, ct).ConfigureAwait(false);
            _read += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_read >= _maxBytes) return 0;
            var remaining = (int)Math.Min(buffer.Length, _maxBytes - _read);
            var limited = buffer.Slice(0, remaining);
            var n = await _inner.ReadAsync(limited, ct).ConfigureAwait(false);
            _read += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
