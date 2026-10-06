namespace Sepius.Application.DTOs;

/// <summary>Respuesta de "¿está el canal en directo?" (misma forma que expone /api/live/{canal}/active).</summary>
public sealed record LiveActiveDto(
    bool IsLive,
    string? Platform,
    string Channel,
    string? HlsUrl,
    bool IsReady);

/// <summary>Estado del transcode de un canal en una plataforma concreta.</summary>
public sealed record LiveStatusDto(bool IsTranscoding, bool IsReady, string HlsUrl);

/// <summary>Una sesión de grabación en curso en el grabador.</summary>
public sealed record LiveSessionDto(string Platform, string Channel, DateTimeOffset StartedAt, string Status);
