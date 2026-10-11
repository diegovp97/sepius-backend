using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sepius.Application.DTOs;
using Sepius.Application.Interfaces;

namespace Sepius.Infrastructure.Recorder;

/// <summary>
/// Pide un clip al grabador. Cliente aparte del de consultas porque cortar tarda varios segundos
/// (el otro tiene 5 s de límite para detectar rápido un grabador caído).
/// </summary>
public sealed class HttpClipRecorder(HttpClient http) : IClipRecorder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ClipCut?> CreateAsync(string channel, string platform, int seconds, int behindSeconds, CancellationToken ct = default)
    {
        var url = $"internal/clips?channel={Uri.EscapeDataString(channel)}&platform={Uri.EscapeDataString(platform)}" +
                  $"&seconds={seconds}&behind={behindSeconds}";
        try
        {
            using var res = await http.PostAsync(url, content: null, ct);
            if (res.StatusCode == HttpStatusCode.NotFound) return null;
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync<ClipCut>(Json, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Tiempo de espera agotado cortando el clip.");
        }
    }
}
