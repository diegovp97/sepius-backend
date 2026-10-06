using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;

namespace Sepius.Infrastructure.Recorder;

/// <summary>
/// Cliente HTTP de la API interna del grabador. Las consultas nunca lanzan: si el grabador no responde
/// (reinicio, caída) devuelven "sin directo" para que el visor muestre el estado apagado en vez de un error.
/// Las órdenes (start/stop) sí propagan el fallo para que el controlador responda 503.
/// </summary>
public sealed class HttpRecorderClient(HttpClient http, ILogger<HttpRecorderClient> logger) : IRecorderClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LiveActiveDto> GetActiveAsync(string channel, CancellationToken ct = default)
    {
        try
        {
            var r = await http.GetFromJsonAsync<LiveActiveDto>($"internal/live/{Uri.EscapeDataString(channel)}/active", Json, ct);
            return r ?? Offline(channel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("[Recorder] No responde al consultar '{Channel}': {Reason}", channel, ex.Message);
            return Offline(channel);
        }
    }

    public async Task<LiveStatusDto> GetStatusAsync(string channel, string platform, CancellationToken ct = default)
    {
        try
        {
            var url = $"internal/live/{Uri.EscapeDataString(channel)}/status?platform={Uri.EscapeDataString(platform)}";
            var r = await http.GetFromJsonAsync<LiveStatusDto>(url, Json, ct);
            if (r is not null) return r;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("[Recorder] No responde al consultar el estado de '{Channel}': {Reason}", channel, ex.Message);
        }
        return new LiveStatusDto(false, false, $"/live/{platform}/{channel}/index.m3u8");
    }

    public async Task<string?> StartAsync(string channel, string platform, CancellationToken ct = default)
    {
        var url = $"internal/live/{Uri.EscapeDataString(channel)}/start?platform={Uri.EscapeDataString(platform)}";
        using var res = await PostAsync(url, ct);
        var body = await res.Content.ReadFromJsonAsync<StartResponse>(Json, ct);
        return body?.HlsUrl;
    }

    public async Task StopAsync(string channel, string platform, CancellationToken ct = default)
    {
        var url = $"internal/live/{Uri.EscapeDataString(channel)}/stop?platform={Uri.EscapeDataString(platform)}";
        using var res = await PostAsync(url, ct);
    }

    /// <summary>
    /// POST de una orden al grabador. Cualquier fallo (rechazo, error HTTP o tiempo agotado) sale como
    /// <see cref="HttpRequestException"/>, que es lo que capturan los controladores para responder 503.
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync(string url, CancellationToken ct)
    {
        try
        {
            var res = await http.PostAsync(url, content: null, ct);
            res.EnsureSuccessStatusCode();
            return res;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Tiempo de espera agotado hablando con el grabador.");
        }
    }

    public async Task<IReadOnlyList<LiveSessionDto>> GetSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<LiveSessionDto>>("internal/live/sessions", Json, ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("[Recorder] No responde al listar sesiones: {Reason}", ex.Message);
            return [];
        }
    }

    private static LiveActiveDto Offline(string channel) => new(false, null, channel, null, false);

    private sealed record StartResponse(string? HlsUrl);
}
