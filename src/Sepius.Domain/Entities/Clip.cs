namespace Sepius.Domain.Entities;

/// <summary>Clip corto del directo creado por un espectador (como los clips de Twitch).</summary>
public class Clip
{
    public Guid Id { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public double DurationSeconds { get; set; }

    /// <summary>Nombre del .mp4 dentro de /recordings/clips (solo el nombre, nunca una ruta).</summary>
    public string FileName { get; set; } = string.Empty;
    public string? ThumbFileName { get; set; }
}
