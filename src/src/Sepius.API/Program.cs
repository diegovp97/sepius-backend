using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sepius.API.Hubs;
using Sepius.API.Workers;
using Sepius.Infrastructure;
using Sepius.Infrastructure.Persistence;
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
            .WithOrigins(origins)
            .AllowAnyMethod()
            .AllowAnyHeader();
    }));

// ── PIPELINE HTTP (Middleware) ────────────────────────────────────────────────
// El orden importa. Equivale a los app.use() de Express.

var app = builder.Build();

// Ejecutar migraciones automáticamente al arrancar
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Sepius v1"));
}

app.UseHttpsRedirection();
app.UseCors("Angular");

// Servir los ficheros HLS estáticos (/live/{channel}/index.m3u8 y segmentos .ts)
// DEBE ir después de UseCors para que los headers CORS se apliquen
app.UseHlsStaticFiles();

app.MapHealthChecks("/health");
app.MapHub<ChatHub>("/hubs/chat");

app.MapControllers();

app.Run();
