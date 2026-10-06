using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Sepius.API.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sepius.API.Hubs;
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

// Infraestructura común (BD, canales, pipeline) + lo propio de la API (login, subidas, cliente del grabador).
// La API ya NO ejecuta ffmpeg/Streamlink ni los workers de Twitch: viven en sepius-recorder, de modo que
// reiniciar la API no interrumpe ninguna grabación.
builder.Services.AddInfrastructureCore(builder.Configuration);
builder.Services.AddApiInfrastructure(builder.Configuration);

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

// Las grabaciones terminadas ya no llegan por un evento en memoria: el grabador (otro proceso) las apunta
// en la tabla upload_pipeline y la cola de subida (YouTubeUploadQueue) las recoge de ahí.

app.Run();
