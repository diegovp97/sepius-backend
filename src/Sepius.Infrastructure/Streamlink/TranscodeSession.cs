using System.Diagnostics;

namespace Sepius.Infrastructure.Streamlink;

public enum TranscodeStatus
{
    Starting,
    Running,
    Stopping,
    Failed,
    Stopped
}

/// <summary>
/// Representa una sesión activa de transcode para un canal.
/// Un único proceso bash ejecuta: streamlink --stdout | ffmpeg -i pipe:0
/// </summary>
public sealed class TranscodeSession
{
    public string Channel { get; }
    public TranscodeStatus Status { get; set; }
    public Process? PipelineProcess { get; set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public TranscodeSession(string channel)
    {
        Channel = channel;
        Status  = TranscodeStatus.Starting;
    }
}
