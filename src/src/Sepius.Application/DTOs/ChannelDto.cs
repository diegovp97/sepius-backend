namespace Sepius.Application.DTOs;

/// <summary>
/// Los records de C# 9+ son clases inmutables con igualdad por valor.
/// Son perfectos para DTOs/ViewModels. En TypeScript equivalen a interfaces
/// de solo lectura o tipos con Readonly&lt;T&gt;.
/// </summary>
public record AddChannelRequest(string Name);

public record ChannelResponse(
    Guid Id,
    string Name,
    bool IsMonitored,
    DateTime AddedAt,
    bool IsCurrentlyRecording
);
