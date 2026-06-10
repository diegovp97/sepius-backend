namespace Sepius.Infrastructure.Streamlink;

public sealed class StreamlinkOptions
{
    public const string SectionName = "Streamlink";

    /// <summary>Directorio base donde se guardarán los .mp4. Mapeado al volumen Docker.</summary>
    public string OutputPath { get; set; } = "/recordings";

    /// <summary>Calidad del stream: best, 1080p, 720p, worst, etc.</summary>
    public string Quality { get; set; } = "best";

    /// <summary>Ruta al ejecutable. En Docker simplemente "streamlink" si está en el PATH.</summary>
    public string ExecutablePath { get; set; } = "streamlink";

    /// <summary>Ruta al ejecutable de ffmpeg. En Docker simplemente "ffmpeg" si está en el PATH.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
    /// --hls-live-restart: empieza desde el inicio si el VOD está disponible.
    /// --retry-streams 5: reintenta la conexión hasta 5 veces.
    /// </summary>
    public string AdditionalArgs { get; set; } = "--hls-live-restart --retry-streams 5 --retry-max 3";
}
