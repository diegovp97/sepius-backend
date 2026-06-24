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
/// Representa una sesión activa de transcode (streamlink + ffmpeg) para un canal.
/// Guarda referencia a ambos procesos y el estado actual.
/// </summary>
public sealed class TranscodeSession
{
    public string Channel { get; }
    public TranscodeStatus Status { get; set; }
    public Process? StreamlinkProcess { get; set; }
    public Process? FfmpegProcess { get; set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public TranscodeSession(string channel)
    {
        Channel = channel;
        Status  = TranscodeStatus.Starting;
    }
}
