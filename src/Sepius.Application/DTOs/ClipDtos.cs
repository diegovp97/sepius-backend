namespace Sepius.Application.DTOs;

/// <summary>Petición de un clip: los últimos <c>Seconds</c> s del directo, terminando <c>BehindSeconds</c> s antes del borde live.</summary>
public sealed record CreateClipRequest(string? Channel, int Seconds, int? BehindSeconds, string? Title, string? Nickname);

public sealed record ClipDto(
    Guid Id,
    string Channel,
    string Platform,
    string Title,
    string Nickname,
    DateTime CreatedAt,
    double DurationSeconds,
    string VideoUrl,
    string? ThumbUrl);

/// <summary>Resultado de cortar un clip en el grabador.</summary>
public sealed record ClipCut(string Id, double DurationSeconds, string FileName, string? ThumbFileName);
