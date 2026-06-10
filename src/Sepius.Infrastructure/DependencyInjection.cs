using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Infrastructure.Persistence;
using Sepius.Infrastructure.Streamlink;
using Sepius.Infrastructure.TwitchApi;

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
        services.Configure<TwitchApiOptions>(
            configuration.GetSection(TwitchApiOptions.SectionName));

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

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider        = new PhysicalFileProvider(liveDir),
            RequestPath         = "/live",
            ContentTypeProvider = contentTypes,
        });

        return app;
    }
}
