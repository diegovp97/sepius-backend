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

    /// <summary>Ruta al ejecutable de yt-dlp. Se usa como fallback cuando Twitch solo entrega audio a Streamlink.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>Archivo cookies.txt de Twitch en formato Netscape. Si existe, yt-dlp lo usa para resolver HLS con vídeo.</summary>
    public string TwitchCookiesPath { get; set; } = "/run/secrets/sepius/twitch-cookies.txt";

    /// <summary>Altura máxima del HLS servido al navegador cuando se usa yt-dlp. 720 es seguro para VPS pequeñas.</summary>
    public int YtDlpTranscodeHeight { get; set; } = 720;

    /// --hls-live-restart: empieza desde el inicio si el VOD está disponible.
    /// --retry-streams 5: reintenta la conexión hasta 5 veces.
    /// </summary>
    public string AdditionalArgs { get; set; } = "--no-config --twitch-supported-codecs=h264,h265,av1 --stream-sorting-excludes '>1080p' --hls-live-restart --retry-streams 5 --retry-max 3";
}
