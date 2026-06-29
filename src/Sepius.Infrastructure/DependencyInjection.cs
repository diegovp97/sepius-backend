using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Sepius.Application.Interfaces;
using Sepius.Infrastructure.Persistence;
using Sepius.Infrastructure.Streamlink;
using Sepius.Infrastructure.TwitchApi;
using Sepius.Infrastructure.YouTube;

namespace Sepius.Infrastructure;

/// <summary>
/// Punto de entrada único para registrar todos los servicios de infraestructura.
///
/// PATRÓN EXTENSION METHOD: Se extiende IServiceCollection para encapsular
/// el registro. Program.cs llama a builder.Services.AddInfrastructure(config)
/// sin necesidad de conocer los detalles internos.
///
/// PARALELO NODE.JS: Es como un módulo de NestJS (providers array) o un
/// plugin de Fastify que registra sus propios servicios.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── CONFIGURACIÓN (IOptions<T>) ──────────────────────────────────────
        // Mapea la sección del JSON a una clase fuertemente tipada.
        // En los servicios se inyecta IOptions<TwitchApiOptions> en lugar de
        // IConfiguration directamente (más testeable y tipado).
        services.AddOptions<TwitchApiOptions>()
            .Bind(configuration.GetSection(TwitchApiOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId),     "TwitchApi__ClientId es obligatorio")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientSecret), "TwitchApi__ClientSecret es obligatorio")
            .ValidateOnStart();

        services.Configure<StreamlinkOptions>(
            configuration.GetSection(StreamlinkOptions.SectionName));

        // ── REPOSITORIOS (Singleton) ─────────────────────────────────────────
        // Singleton = una sola instancia para toda la vida de la app.
        // Correcto para almacenamiento en memoria que debe persistir entre requests.
        services.AddSingleton<IChannelRepository, InMemoryChannelRepository>();

        // ── SERVICIOS DE PROCESO (Singleton) ────────────────────────────────
        // Los procesos del SO deben sobrevivir entre peticiones HTTP.
        // Al ser Singleton y IDisposable, el host lo destruye correctamente al apagarse.
        services.AddSingleton<IStreamlinkService, StreamlinkService>();

        // Singleton para el pipeline streamlink→ffmpeg→HLS (también IDisposable)
        services.AddSingleton<ILiveTranscodeService, LiveTranscodeService>();

        // ── HTTP CLIENT (Twitch API) ─────────────────────────────────────────
        // AddHttpClient registra TwitchApiService como Singleton con un HttpClient
        // gestionado por IHttpClientFactory (evita socket exhaustion).
        // El HttpClient se crea una vez y se reutiliza. Es el equivalente a crear
        // una instancia global de axios con configuración base.
        services.AddHttpClient<ITwitchApiService, TwitchApiService>();

        // Cliente HTTP nombrado para la API pública de Kick
        services.AddHttpClient("Kick", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // ── YOUTUBE UPLOAD ───────────────────────────────────────────────────
        services.Configure<YouTubeOptions>(configuration.GetSection(YouTubeOptions.SectionName));
        services.AddHttpClient<IYouTubeUploadService, YouTubeUploadService>(client =>
        {
            client.Timeout = TimeSpan.FromHours(6);
        });

        // Cola de subidas a YouTube — BackgroundService que procesa encolados
        services.AddSingleton<YouTubeUploadQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<YouTubeUploadQueue>());

        // ── BASE DE DATOS (PostgreSQL + EF Core) ─────────────────────────────
        var rawConn = configuration.GetConnectionString("Postgres") ?? "";
        // Render provee la URL en formato postgresql://user:pass@host/db
        // Npgsql necesita Host=...;Database=...;Username=...;Password=...
        string connectionString;
        if (rawConn.StartsWith("postgresql://") || rawConn.StartsWith("postgres://"))
        {
            var uri = new Uri(rawConn);
            var userInfo = uri.UserInfo.Split(':');
            var port = uri.Port > 0 ? uri.Port : 5432;
            connectionString = $"Host={uri.Host};Port={port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=true";
        }
        else
        {
            connectionString = rawConn;
        }
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(connectionString));

        return services;
    }

    /// <summary>
    /// Registra el middleware de ficheros estáticos para los segmentos HLS del live.
    /// Llámalo en Program.cs DESPUÉS de app.UseCors() y ANTES de app.MapControllers().
    /// </summary>
    public static WebApplication UseHlsStaticFiles(this WebApplication app)
    {
        var streamlinkOptions = app.Services
            .GetRequiredService<IOptions<StreamlinkOptions>>().Value;

        var liveDir = Path.Combine(streamlinkOptions.OutputPath, "live");
        Directory.CreateDirectory(liveDir);

        // Registrar tipos MIME que ASP.NET Core no reconoce por defecto
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
        contentTypes.Mappings[".ts"]   = "video/mp2t";
        contentTypes.Mappings[".m4s"]  = "video/iso.segment";   // fMP4 media segment (CMAF)
        contentTypes.Mappings[".mp4"]  = "video/mp4";            // fMP4 init segment

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider        = new PhysicalFileProvider(liveDir),
            RequestPath         = "/live",
            ContentTypeProvider = contentTypes,
            // Los segmentos HLS en vivo no deben cachearse — cambian cada segundo
            OnPrepareResponse   = ctx =>
            {
                ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                ctx.Context.Response.Headers["Pragma"]        = "no-cache";
                ctx.Context.Response.Headers["Expires"]       = "0";
            }
        });

        return app;
    }
}
