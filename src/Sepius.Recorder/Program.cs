using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;
using Sepius.Infrastructure;
using Sepius.Infrastructure.Persistence;
using Sepius.Infrastructure.Pipeline;
using Sepius.Recorder;
using Sepius.Recorder.Workers;

// ══════════════════════════════════════════════════════════════════════════════
// SEPIUS RECORDER — el grabador
//
// Aquí viven los procesos largos (ffmpeg, Streamlink, yt-dlp) y los workers que detectan los directos
// (Twitch EventSub, polling de Kick). Está separado de la API a propósito: así se puede reiniciar y
// desplegar la API en cualquier momento sin cortar ninguna grabación.
//
//   sepius-api ──HTTP interno (clave)──▶ sepius-recorder ──▶ ffmpeg / streamlink
//        ▲                                      │
//        └──── upload_pipeline (Postgres) ◀─────┘   (el grabador apunta lo que termina; la API lo sube)
// ══════════════════════════════════════════════════════════════════════════════

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructureCore(builder.Configuration);
builder.Services.AddRecorderInfrastructure(builder.Configuration);

builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection(MonitorOptions.SectionName));
builder.Services.AddHostedService<TwitchEventSubWorker>();
builder.Services.AddHostedService<TwitchMonitorWorker>();
// El último en registrarse es el primero en detenerse: cierra las grabaciones antes que nada.
builder.Services.AddHostedService<RecorderShutdownService>();
builder.Services.AddHealthChecks();

// Debe superar a RecorderShutdownService.GracePeriod (80 s) y ser menor que stop_grace_period de Docker (90 s).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(85));

var app = builder.Build();

// ── Base de datos: migraciones (la API también las aplica; EF usa un bloqueo, así que es seguro) y canales
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    for (var attempt = 1; ; attempt++)
    {
        try { await db.Database.MigrateAsync(); break; }
        catch (Exception ex) when (attempt < 10)
        {
            app.Logger.LogWarning("Base de datos no disponible aún ({Reason}); reintento {N}/10…", ex.Message, attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    // Sembrar los canales configurados en Monitor__Channels (idempotente: la API hace lo mismo)
    var monitorOpts = scope.ServiceProvider.GetRequiredService<IOptions<MonitorOptions>>().Value;
    var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
    foreach (var name in monitorOpts.Channels.Where(n => !string.IsNullOrWhiteSpace(n)))
    {
        if (await channelRepo.GetByNameAsync(name) is null)
            await channelRepo.AddAsync(Channel.Create(name));
    }
}

app.MapHealthChecks("/health");
RecorderEndpoints.Map(app);

// Cuando termina una grabación: se apunta en upload_pipeline (si dura lo suficiente) y la API la sube.
{
    var live = app.Services.GetRequiredService<ILiveTranscodeService>();
    var intake = app.Services.GetRequiredService<RecordingIntake>();
    live.RecordingCompleted += async recording => { await intake.HandleAsync(recording); };
}

app.Run();
