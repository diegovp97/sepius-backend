using Sepius.Domain.Entities;

namespace Sepius.Application.DTOs;

public record RecordingResponse(
    Guid Id,
    string ChannelName,
    string FileName,
    DateTime StartedAt,
    DateTime? EndedAt,
    RecordingStatus Status,
    long FileSizeBytes
);
