using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;

namespace Sepius.Infrastructure.TwitchApi;

/// <summary>
/// Implementación del cliente para la API Helix de Twitch.
/// Gestiona automáticamente el ciclo de vida del token OAuth2 (Client Credentials Flow).
///
/// PATRÓN HttpClient: NO crear instancias de HttpClient directamente con `new`.
/// El problema (socket exhaustion) es equivalente al de no reusar instancias
/// de axios en Node.js. La solución de .NET es IHttpClientFactory, que gestiona
/// el pool de conexiones. El HttpClient se inyecta por DI.
///
/// REGISTRO: Se registra como Singleton vía AddHttpClient(...) para que el
/// estado del token (accessToken, tokenExpiry) persista entre llamadas.
/// </summary>
public sealed class TwitchApiService : ITwitchApiService
{
    private readonly HttpClient _httpClient;
    private readonly TwitchApiOptions _options;
    private readonly ILogger<TwitchApiService> _logger;

    // SemaphoreSlim(1,1) actúa como un mutex async-compatible.
    // En Node.js usarías una variable booleana isRefreshing y una Promise
    // para serializar las llamadas de refresh. Este patrón es más robusto.
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string _accessToken = string.Empty;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public TwitchApiService(
        HttpClient httpClient,
        IOptions<TwitchApiOptions> options,
        ILogger<TwitchApiService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> IsChannelLiveAsync(string channelName, CancellationToken ct = default)
    {
        await EnsureValidTokenAsync(ct);

        var url = $"{_options.ApiBaseUrl}/streams?user_login={Uri.EscapeDataString(channelName)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Client-Id", _options.ClientId);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");

        var response = await _httpClient.SendAsync(request, ct);

        // Si el token expiró antes de tiempo, forzar refresh y reintentar UNA vez
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Token de Twitch rechazado (401). Forzando renovación...");
            _tokenExpiry = DateTime.MinValue;
            await EnsureValidTokenAsync(ct);
            return await IsChannelLiveAsync(channelName, ct);
        }

        response.EnsureSuccessStatusCode();

        // ReadFromJsonAsync<T> es la alternativa tipada a response.json() de fetch/axios
        var content = await response.Content.ReadFromJsonAsync<TwitchStreamsResponse>(
            cancellationToken: ct);

        return content?.Data.Any(s => s.Type == "live") ?? false;
    }

    private async Task EnsureValidTokenAsync(CancellationToken ct)
    {
        // Salida rápida si el token sigue siendo válido (con margen de 5 minutos)
        if (DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            return;

        // Adquirir el lock antes de verificar de nuevo (Double-Checked Locking)
        // Evita que múltiples llamadas concurrentes pidan el token a la vez
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return; // Otro hilo ya refrescó el token mientras esperábamos

            _logger.LogInformation("Obteniendo/renovando token de acceso de Twitch...");

            var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "client_credentials"
            });

            var tokenResponse = await _httpClient.PostAsync(_options.TokenEndpoint, tokenBody, ct);
            tokenResponse.EnsureSuccessStatusCode();

            var tokenData = await tokenResponse.Content.ReadFromJsonAsync<TwitchTokenResponse>(
                cancellationToken: ct)
                ?? throw new InvalidOperationException("Respuesta de token de Twitch vacía.");

            _accessToken = tokenData.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn);

            _logger.LogInformation("Token de Twitch renovado. Expira: {Expiry:u}", _tokenExpiry);
        }
        finally
        {
            // CRÍTICO: siempre liberar el semáforo, incluso si hay excepciones.
            // Equivalente al bloque finally en promesas de Node.js.
            _tokenLock.Release();
        }
    }
}
