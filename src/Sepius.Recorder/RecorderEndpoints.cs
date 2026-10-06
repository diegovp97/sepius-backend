using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;
using Sepius.Infrastructure.Recorder;

namespace Sepius.Recorder;

/// <summary>
/// API interna del grabador. Solo es alcanzable desde la red de Docker y exige la clave compartida
/// (cabecera X-Recorder-Key). La API pública (sepius-api) es la única que la usa.
/// </summary>
public static class RecorderEndpoints
{
    private static readonly string[] Platforms = ["kick", "twitch"];

    public static void Map(WebApplication app)
    {
        // Clave compartida en tiempo constante. /health queda abierto para el healthcheck de Docker.
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/internal"), branch =>
            branch.Use(async (ctx, next) =>
            {
                var expected = ctx.RequestServices.GetRequiredService<IOptions<RecorderOptions>>().Value.Key;
                var given = ctx.Request.Headers[RecorderOptions.KeyHeader].ToString();
                if (!FixedTimeEquals(given, expected))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next();
            }));

        var live = app.MapGroup("/internal/live");

        live.MapGet("/sessions", (ILiveTranscodeService svc) => Results.Ok(svc.GetActiveSessions()));

        live.MapGet("/{channel}/active", (string channel, ILiveTranscodeService svc) =>
        {
            foreach (var platform in Platforms)
            {
                if (!svc.IsTranscoding(channel, platform)) continue;
                return Results.Ok(new LiveActiveDto(
                    IsLive: true, Platform: platform, Channel: channel,
                    HlsUrl: svc.GetHlsUrl(channel, platform),
                    IsReady: svc.IsHlsReady(channel, platform)));
            }
            return Results.Ok(new LiveActiveDto(false, null, channel, null, false));
        });

        live.MapGet("/{channel}/status", (string channel, string? platform, ILiveTranscodeService svc) =>
        {
            platform ??= "twitch";
            return Results.Ok(new LiveStatusDto(
                svc.IsTranscoding(channel, platform),
                svc.IsHlsReady(channel, platform),
                svc.GetHlsUrl(channel, platform)));
        });

        live.MapPost("/{channel}/start", async (string channel, string? platform, ILiveTranscodeService svc, CancellationToken ct) =>
        {
            platform ??= "twitch";
            try
            {
                await svc.StartAsync(channel, platform, ct);
                return Results.Ok(new { hlsUrl = svc.GetHlsUrl(channel, platform) });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        live.MapPost("/{channel}/stop", async (string channel, string? platform, ILiveTranscodeService svc) =>
        {
            await svc.StopAsync(channel, platform ?? "twitch");
            return Results.NoContent();
        });
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }
}
