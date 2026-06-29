using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sepius.API.Hubs;
using Sepius.API.Workers;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;
using Sepius.Infrastructure;
using Sepius.Infrastructure.Persistence;
using Sepius.Infrastructure.YouTube;
// ══════════════════════════════════════════════════════════════════════════════
// PROGRAM.CS — El punto de entrada de la app y configuración del contenedor DI
//
// En Node.js/Express este fichero equivale a tu app.js o server.ts:
//   const app = express()
//   app.use(cors())
//   app.use(express.json())
//   app.listen(8080)
//
// La diferencia clave: aquí separamos la fase de REGISTRO de servicios
// (builder.*) de la fase de CONFIGURACIÓN del pipeline HTTP (app.*).
// ══════════════════════════════════════════════════════════════════════════════

var builder = WebApplication.CreateBuilder(args);

// ── SERVICIOS / CONTENEDOR DI ─────────────────────────────────────────────────

// Registrar toda la capa de infraestructura via el extension method
builder.Services.AddInfrastructure(builder.Configuration);

// Registrar opciones del worker y el worker en sí como HostedService
// AddHostedService = el host lo inicia/detiene automáticamente
builder.Services.Configure<MonitorOptions>(
    builder.Configuration.GetSection(MonitorOptions.SectionName));
// TwitchEventSubWorker: eventos Twitch en tiempo real vía WebSocket
builder.Services.AddHostedService<TwitchEventSubWorker>();
// TwitchMonitorWorker: polling para plataformas no-Twitch (Kick)
builder.Services.AddHostedService<TwitchMonitorWorker>();

// Controladores con serialización de enums como strings
// (envía "Completed" al frontend en lugar del entero 1)
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Swagger / OpenAPI — documentación interactiva de la API
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "Sepius Recording API", Version = "v1" }));

// Health Checks — usado por Docker healthcheck y Kubernetes liveness probe
builder.Services.AddHealthChecks();

// SignalR — WebSockets para el chat en tiempo real
builder.Services.AddSignalR();

// CORS para el frontend Angular
// En producción, la URL viene de la variable de entorno AllowedOrigins
builder.Services.AddCors(options =>
    options.AddPolicy("Angular", policy =>
    {
        var origins = (builder.Configuration.GetValue<string>("AllowedOrigins") ?? "http://localhost:4200")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (origins.Contains("*"))
                    return true;
                // En desarrollo: cualquier puerto de localhost está permitido
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                    (uri.Host == "localhost" || uri.Host == "127.0.0.1"))
                    return true;
                // En producción: solo los orígenes configurados
                return origins.Contains(origin, StringComparer.OrdinalIgnoreCase);
            })
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    }));

// ── PIPELINE HTTP (Middleware) ────────────────────────────────────────────────
// El orden importa. Equivale a los app.use() de Express.

var app = builder.Build();

// Ejecutar migraciones automáticamente al arrancar
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Sembrar canales configurados en Monitor__Channels
    var monitorOpts = scope.ServiceProvider.GetRequiredService<IOptions<MonitorOptions>>().Value;
    var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
    foreach (var name in monitorOpts.Channels.Where(n => !string.IsNullOrWhiteSpace(n)))
    {
        var existing = await channelRepo.GetByNameAsync(name);
        if (existing is null)
            await channelRepo.AddAsync(Channel.Create(name));
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Sepius v1"));
}

// CORS ANTES de HTTPS redirect y cualquier otro middleware que responda
app.UseCors("Angular");
app.UseHttpsRedirection();

// Servir los ficheros HLS estáticos (/live/{channel}/index.m3u8 y segmentos .ts)
// DEBE ir después de UseCors para que los headers CORS se apliquen
app.UseHlsStaticFiles();

app.MapHealthChecks("/health");
app.MapHub<ChatHub>("/hubs/chat");

app.MapControllers();

// ── WIRING DE EVENTOS ────────────────────────────────────────────────────
// Suscribir RecordingCompleted de LiveTranscodeService a la cola de YouTube.
// El upload automático pasa por la cola para evitar subidas concurrentes.
{
    var liveTranscode = app.Services.GetRequiredService<ILiveTranscodeService>();
    var uploadQueue = app.Services.GetRequiredService<YouTubeUploadQueue>();

    liveTranscode.RecordingCompleted += (recording) =>
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation(
            "Grabación completada para '{Channel}'. Encolando subida a YouTube...",
            recording.ChannelName);
        uploadQueue.Enqueue(recording);
        return Task.CompletedTask;
    };
}

app.Run();
