using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.Persistence;
using Sepius.Infrastructure.Streamlink;

namespace Sepius.API.Controllers;

/// <summary>
/// Clips del directo (como los de Twitch). El corte lo hace el grabador, que tiene el HLS y ffmpeg; esta API
/// registra el clip, lo lista y sirve los ficheros desde /clips/{nombre}.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed partial class ClipsController(
    AppDbContext db,
    IRecorderClient recorder,
    IClipRecorder clipRecorder,
    IOptions<StreamlinkOptions> streamlink,
    ILogger<ClipsController> logger) : ControllerBase
{
    private const int MaxClips = 300;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    [GeneratedRegex("^[a-z0-9_]{1,25}$")]
    private static partial Regex ChannelRegex();

    [HttpPost]
    [EnableRateLimiting("clip")]
    [ProducesResponseType(typeof(ClipDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateClipRequest req, CancellationToken ct)
    {
        var channel = string.IsNullOrWhiteSpace(req.Channel) ? "elttblue" : req.Channel.Trim().ToLowerInvariant();
        if (!ChannelRegex().IsMatch(channel))
            return BadRequest(new { error = "Canal inválido." });

        var seconds = Math.Clamp(req.Seconds, ClipService.MinSeconds, ClipService.MaxSeconds);
        var behind  = Math.Clamp(req.BehindSeconds ?? 0, 0, ClipService.MaxBehindSeconds);
        var title   = Clean(req.Title, 80);
        var nick    = Clean(req.Nickname, 32);
        if (title.Length == 0) title = $"Clip de {channel}";
        if (nick.Length == 0)  nick  = "anónimo";

        var active = await recorder.GetActiveAsync(channel, ct);
        if (!active.IsLive || active.Platform is null)
            return Conflict(new { error = $"{channel} no está en directo ahora mismo." });

        ClipCut? cut;
        try
        {
            cut = await clipRecorder.CreateAsync(channel, active.Platform, seconds, behind, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("[Clips] El grabador no pudo cortar el clip: {Reason}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "No se pudo crear el clip ahora mismo. Inténtalo de nuevo." });
        }

        if (cut is null)
            return Conflict(new { error = "Todavía no hay suficiente vídeo para un clip. Espera unos segundos." });

        var clip = new Clip
        {
            Id              = Guid.ParseExact(cut.Id, "N"),
            Channel         = channel,
            Platform        = active.Platform,
            Title           = title,
            Nickname        = nick,
            CreatedAt       = DateTime.UtcNow,
            DurationSeconds = cut.DurationSeconds,
            FileName        = cut.FileName,
            ThumbFileName   = cut.ThumbFileName,
        };
        db.Clips.Add(clip);
        await db.SaveChangesAsync(ct);

        await PruneAsync(ct);
        return Created($"/api/clips/{clip.Id}", ToDto(clip));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ClipDto>>> List([FromQuery] int take = 24, [FromQuery] int skip = 0, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 48);
        skip = Math.Max(skip, 0);
        var rows = await db.Clips.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Skip(skip).Take(take)
            .ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ClipDto>> Get(Guid id, CancellationToken ct)
    {
        var clip = await db.Clips.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return clip is null ? NotFound() : Ok(ToDto(clip));
    }

    [Authorize]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var clip = await db.Clips.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (clip is null) return NotFound();
        DeleteFiles(clip);
        db.Clips.Remove(clip);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ClipDto ToDto(Clip c) => new(
        c.Id, c.Channel, c.Platform, c.Title, c.Nickname, c.CreatedAt, c.DurationSeconds,
        $"/clips/{c.FileName}",
        c.ThumbFileName is null ? null : $"/clips/{c.ThumbFileName}");

    /// <summary>Quita caracteres de control y recorta. El texto lo muestra Angular escapado, nunca como HTML.</summary>
    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var chars = s.Where(ch => !char.IsControl(ch)).ToArray();
        var t = new string(chars).Trim();
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>Retención: los clips de más de 30 días y todo lo que pase de 300 se borra (fichero y fila).</summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            var old = await db.Clips.Where(c => c.CreatedAt < cutoff).ToListAsync(ct);

            var extra = await db.Clips.OrderByDescending(c => c.CreatedAt).Skip(MaxClips).ToListAsync(ct);

            foreach (var c in old.Concat(extra).DistinctBy(c => c.Id))
            {
                DeleteFiles(c);
                db.Clips.Remove(c);
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Clips] Error aplicando la retención de clips.");
        }
    }

    private void DeleteFiles(Clip c)
    {
        var dir = ClipService.ClipsDirectory(streamlink.Value);
        foreach (var name in new[] { c.FileName, c.ThumbFileName })
        {
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                var path = Path.Combine(dir, Path.GetFileName(name)); // GetFileName: nunca sale de la carpeta
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Clips] No se pudo borrar {Name}", name);
            }
        }
    }
}
