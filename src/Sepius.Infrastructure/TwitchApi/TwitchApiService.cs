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
    private string _userAccessToken = string.Empty;
    private DateTime _userTokenExpiry = DateTime.MinValue;

    public TwitchApiService(
        HttpClient httpClient,
        IOptions<TwitchApiOptions> options,
        ILogger<TwitchApiService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> GetUserIdAsync(string channelName, CancellationToken ct = default)
    {
        await EnsureValidTokenAsync(ct);

        var url = $"{_options.ApiBaseUrl}/users?login={Uri.EscapeDataString(channelName)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Client-Id", _options.ClientId);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadFromJsonAsync<TwitchUsersResponse>(cancellationToken: ct);
        return content?.Data.FirstOrDefault()?.Id;
    }

    public async Task SubscribeToStreamEventsAsync(
        string sessionId,
        string broadcasterId,
        string eventType,
        CancellationToken ct = default)
    {
        await EnsureValidUserTokenAsync(ct);

        if (string.IsNullOrWhiteSpace(_userAccessToken))
        {
            _logger.LogWarning(
                "No hay user access token disponible. Saltando suscripción EventSub para {BroadcasterId}. " +
                "Configura TwitchApi__RefreshToken o completa el Authorization Code Flow.",
                broadcasterId);
            return;
        }

        var body = new EventSubSubscriptionRequest
        {
            Type = eventType,
            Version = "1",
            Condition = new EventSubCondition { BroadcasterUserId = broadcasterId },
            Transport = new EventSubTransport { Method = "websocket", SessionId = sessionId }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl}/eventsub/subscriptions");
        request.Headers.TryAddWithoutValidation("Client-Id", _options.ClientId);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_userAccessToken}");
        request.Content = JsonContent.Create(body);

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Error suscribiendo a {EventType} para {BroadcasterId}: {Status} {Error}",
                eventType, broadcasterId, response.StatusCode, error);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation("Suscrito a {EventType} para broadcaster {BroadcasterId}", eventType, broadcasterId);
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

    private static bool IsTokenValid(DateTime expiry)
        // Comprobamos que la expiración menos 5 min sea futura, guardando
        // contra DateTime.MinValue que lanzaría ArgumentOutOfRangeException.
        => expiry > DateTime.MinValue.AddMinutes(6)
           && DateTime.UtcNow < expiry.AddMinutes(-5);

    private async Task EnsureValidTokenAsync(CancellationToken ct)
    {
        // Salida rápida si el token sigue siendo válido (con margen de 5 minutos)
        if (IsTokenValid(_tokenExpiry))
            return;

        // Adquirir el lock antes de verificar de nuevo (Double-Checked Locking)
        // Evita que múltiples llamadas concurrentes pidan el token a la vez
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (IsTokenValid(_tokenExpiry))
                return; // Otro hilo ya refrescó el token mientras esperábamos

            _logger.LogInformation(
                "Obteniendo/renovando app access token de Twitch. ClientIdLength={ClientIdLength}",
                _options.ClientId?.Length ?? 0);

            var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = _options.ClientId!,
                ["client_secret"] = _options.ClientSecret!,
                ["grant_type"]    = "client_credentials"
            });

            var tokenResponse = await _httpClient.PostAsync(_options.TokenEndpoint, tokenBody, ct);

            var responseBody = await tokenResponse.Content.ReadAsStringAsync(ct);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Error obteniendo app access token de Twitch. Status={StatusCode}. Body={Body}",
                    tokenResponse.StatusCode,
                    responseBody);
                tokenResponse.EnsureSuccessStatusCode();
            }

            var tokenData = System.Text.Json.JsonSerializer.Deserialize<TwitchTokenResponse>(
                responseBody,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Respuesta de token de Twitch vacía.");

            _accessToken = tokenData.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn);

            _logger.LogInformation("App access token renovado. Expira: {Expiry:u}", _tokenExpiry);
        }
        finally
        {
            // CRÍTICO: siempre liberar el semáforo, incluso si hay excepciones.
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Asegura que el user access token sea válido. Si no lo es o no existe,
    /// intenta refrescarlo usando el refresh_token configurado.
    /// </summary>
    private async Task EnsureValidUserTokenAsync(CancellationToken ct)
    {
        // Si el token en memoria sigue válido, salir rápido
        if (IsTokenValid(_userTokenExpiry) && !string.IsNullOrWhiteSpace(_userAccessToken))
            return;

        // Si hay un UserAccessToken estático configurado y no tenemos refresh_token, usarlo directamente
        if (!string.IsNullOrWhiteSpace(_options.UserAccessToken) && string.IsNullOrWhiteSpace(_options.RefreshToken))
        {
            _userAccessToken = _options.UserAccessToken;
            _userTokenExpiry = DateTime.MaxValue; // No expira controlado por nosotros
            return;
        }

        // Si no hay refresh_token, no podemos refrescar
        if (string.IsNullOrWhiteSpace(_options.RefreshToken))
        {
            _logger.LogWarning("No hay RefreshToken configurado. No se puede obtener user access token.");
            _userAccessToken = string.Empty;
            return;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Double-checked locking
            if (IsTokenValid(_userTokenExpiry) && !string.IsNullOrWhiteSpace(_userAccessToken))
                return;

            _logger.LogInformation("Refrescando user access token usando refresh_token...");

            var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = _options.ClientId!,
                ["client_secret"] = _options.ClientSecret!,
                ["refresh_token"] = _options.RefreshToken!,
                ["grant_type"]    = "refresh_token"
            });

            var tokenResponse = await _httpClient.PostAsync(_options.TokenEndpoint, tokenBody, ct);
            var responseBody = await tokenResponse.Content.ReadAsStringAsync(ct);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Error refrescando user access token. Status={StatusCode}. Body={Body}. " +
                    "Es posible que el refresh_token haya expirado. Ejecuta el Authorization Code Flow de nuevo.",
                    tokenResponse.StatusCode,
                    responseBody);
                _userAccessToken = string.Empty;
                return;
            }

            var tokenData = System.Text.Json.JsonSerializer.Deserialize<TwitchTokenResponse>(
                responseBody,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Respuesta de token de Twitch vacía.");

            _userAccessToken = tokenData.AccessToken;
            _userTokenExpiry = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn);

            // Twitch devuelve un nuevo refresh_token en cada refresh
            if (!string.IsNullOrWhiteSpace(tokenData.RefreshToken))
            {
                _options.RefreshToken = tokenData.RefreshToken;
                _logger.LogInformation("Nuevo refresh_token recibido de Twitch.");
            }

            _logger.LogInformation("User access token refrescado. Expira: {Expiry:u}", _userTokenExpiry);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Intercambia un authorization code por un user access token y refresh token.
    /// Llama esto una vez después de completar el flujo de autorización en el navegador.
    /// </summary>
    public async Task<(string AccessToken, string RefreshToken)> ExchangeCodeAsync(
        string code,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Intercambiando authorization code por tokens...");

        var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["code"]          = code,
            ["grant_type"]    = "authorization_code",
            ["redirect_uri"]  = _options.RedirectUri
        });

        var tokenResponse = await _httpClient.PostAsync(_options.TokenEndpoint, tokenBody, ct);
        var responseBody = await tokenResponse.Content.ReadAsStringAsync(ct);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Error intercambiando authorization code. Status={StatusCode}. Body={Body}",
                tokenResponse.StatusCode,
                responseBody);
            tokenResponse.EnsureSuccessStatusCode();
        }

        var tokenData = System.Text.Json.JsonSerializer.Deserialize<TwitchTokenResponse>(
            responseBody,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Respuesta de token de Twitch vacía.");

        _logger.LogInformation(
            "Authorization code intercambiado exitosamente. Access token expira en {ExpiresIn}s",
            tokenData.ExpiresIn);

        return (tokenData.AccessToken, tokenData.RefreshToken ?? string.Empty);
    }
}
