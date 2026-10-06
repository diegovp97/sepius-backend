using Sepius.Application.Interfaces;

namespace Sepius.Recorder;

/// <summary>
/// Al apagar el grabador (docker stop / redeploy del propio grabador) cierra las grabaciones con limpieza:
/// SIGINT a ffmpeg para que el MP4 quede bien cerrado, y espera a que se apunte en la cola de subida.
/// Se registra el último para que se detenga el primero (los HostedService paran en orden inverso).
/// </summary>
public sealed class RecorderShutdownService(ILiveTranscodeService live, ILogger<RecorderShutdownService> logger) : IHostedService
{
    /// <summary>Menos que ShutdownTimeout del host y que stop_grace_period de Docker.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(80);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Apagando el grabador: cerrando grabaciones en curso…");
        await live.StopAllAsync(GracePeriod);
    }
}
