using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Sepius.Application.Interfaces;
using Sepius.Infrastructure.Persistence;
using Sepius.Infrastructure.Recorder;
using Sepius.Infrastructure.Streamlink;
using Sepius.Infrastructure.TwitchApi;
using Sepius.Infrastructure.Drive;
using Sepius.Infrastructure.Pipeline;
using Sepius.Infrastructure.YouTube;
using Sepius.Infrastructure.Services;

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
    /// <summary>
    /// Lo que necesitan los DOS procesos (API y grabador): base de datos, canales y estado del pipeline.
    /// </summary>
    public static IServiceCollection AddInfrastructureCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Ruta de grabaciones (volumen compartido): la API sirve el HLS y los .mp4, el grabador los escribe.
        services.Configure<StreamlinkOptions>(configuration.GetSection(StreamlinkOptions.SectionName));
        services.Configure<PipelineOptions>(configuration.GetSection(PipelineOptions.SectionName));

        // Clave compartida API ↔ grabador (Recorder__Key): sin ella no arranca ninguno de los dos.
        services.AddOptions<RecorderOptions>()
            .Bind(configuration.GetSection(RecorderOptions.SectionName))
            .Validate(o => o.Key.Length >= 16, "Recorder__Key es obligatorio y debe tener al menos 16 caracteres")
            .ValidateOnStart();

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

        // Canales en Postgres: los dos procesos tienen que ver la misma lista.
        services.AddSingleton<IChannelRepository, EfChannelRepository>();

        // Estado persistente del pipeline (grabación → Drive → YouTube) en Postgres
        services.AddSingleton<UploadPipelineStore>();

        return services;
    }

    /// <summary>
    /// Solo la API: login, subidas a Drive/YouTube (cola) y el cliente que habla con el grabador.
    /// La API ya NO ejecuta ffmpeg ni Streamlink.
    /// </summary>
    public static IServiceCollection AddApiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<IAuthService, AuthService>();

        // ── YOUTUBE UPLOAD ───────────────────────────────────────────────────
        services.Configure<YouTubeOptions>(configuration.GetSection(YouTubeOptions.SectionName));
        services.AddHttpClient<IYouTubeUploadService, YouTubeUploadService>(client =>
        {
            client.Timeout = TimeSpan.FromHours(6);
        });

        // Copia de seguridad en Google Drive (desactivada por defecto: GoogleDrive__Enabled)
        services.Configure<GoogleDriveOptions>(configuration.GetSection(GoogleDriveOptions.SectionName));
        services.AddHttpClient<IDriveUploadService, GoogleDriveService>(client =>
        {
            client.Timeout = TimeSpan.FromHours(6);
        });

        // Cola del pipeline: recoge las grabaciones que apunta el grabador y las sube (Drive → YouTube)
        services.AddSingleton<YouTubeUploadQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<YouTubeUploadQueue>());

        // Cliente de la API interna del grabador (red de Docker, con clave)
        services.AddHttpClient<IRecorderClient, HttpRecorderClient>((sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<RecorderOptions>>().Value;
            client.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add(RecorderOptions.KeyHeader, o.Key);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        return services;
    }

    /// <summary>
    /// Solo el grabador: Twitch/Kick, ffmpeg/Streamlink y el apunte de grabaciones terminadas.
    /// </summary>
    public static IServiceCollection AddRecorderInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TwitchApiOptions>()
            .Bind(configuration.GetSection(TwitchApiOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId),     "TwitchApi__ClientId es obligatorio")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientSecret), "TwitchApi__ClientSecret es obligatorio")
            .ValidateOnStart();

        // Singleton para el pipeline streamlink→ffmpeg→HLS (también IDisposable)
        services.AddSingleton<ILiveTranscodeService, LiveTranscodeService>();
        services.AddSingleton<RecordingIntake>();

        services.AddHttpClient<ITwitchApiService, TwitchApiService>();

        // Cliente HTTP nombrado para la API pública de Kick
        services.AddHttpClient("Kick", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

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
