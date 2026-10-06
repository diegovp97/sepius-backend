using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Sepius.API.Auth;
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

// ── AUTENTICACIÓN (JWT) ───────────────────────────────────────────────────────
// La clave y la contraseña de admin SIEMPRE vienen de la configuración
// (Auth__JwtKey / Auth__AdminPassword). No hay valores por defecto en el código.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(o => o.JwtKey.Length >= 32, "Auth__JwtKey es obligatorio y debe tener al menos 32 caracteres")
    .Validate(o => o.AdminPassword.Length >= 12, "Auth__AdminPassword es obligatorio y debe tener al menos 12 caracteres")
    .ValidateOnStart();
builder.Services.AddSingleton<TokenService>();

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSection.Issuer,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenService.BuildKey(jwtSection.JwtKey.PadRight(32, '_')),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        // El reproductor <video> y los enlaces de descarga no pueden enviar cabeceras:
        // para /api/recordings/stream se acepta el token en ?access_token=
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api/recordings/stream") &&
                    ctx.Request.Query.TryGetValue("access_token", out var t))
                    ctx.Token = t;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// Límite de intentos de login: 5 por minuto y por IP
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});

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

    // Crear tabla auth_users si no existe y fijar la contraseña de admin desde la configuración
    var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS auth_users (
                id SERIAL PRIMARY KEY,
                username TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );";
        await cmd.ExecuteNonQueryAsync();
    }
    await conn.CloseAsync();

    var jwtOpts = scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
    await scope.ServiceProvider.GetRequiredService<IAuthService>()
        .SetPasswordAsync("admin", jwtOpts.AdminPassword);

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

// Detrás de Caddy: respetar X-Forwarded-For/Proto (solo de proxies de confianza = loopback)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// CORS ANTES de HTTPS redirect y cualquier otro middleware que responda
app.UseCors("Angular");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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

    // Las grabaciones muy cortas (p. ej. el clip de anuncio de Twitch de ~16 s o un arranque fallido)
    // no se suben a Drive ni a YouTube. Configurable con Pipeline__MinDurationMinutes (0 = subir todo).
    var minDuration = TimeSpan.FromMinutes(Math.Max(0, app.Configuration.GetValue<int?>("Pipeline:MinDurationMinutes") ?? 5));

    liveTranscode.RecordingCompleted += (recording) =>
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();

        if (recording.Duration < minDuration)
        {
            logger.LogWarning(
                "Grabación de '{Channel}' demasiado corta ({Seconds:F0} s < {Min} min): no se sube. Fichero: {File}",
                recording.ChannelName, recording.Duration.TotalSeconds, minDuration.TotalMinutes, recording.FileName);
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Grabación completada para '{Channel}'. Encolando subida (Drive → YouTube)...",
            recording.ChannelName);
        uploadQueue.Enqueue(recording);
        return Task.CompletedTask;
    };
}

app.Run();
