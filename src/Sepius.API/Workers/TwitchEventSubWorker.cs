using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Infrastructure.TwitchApi;

namespace Sepius.API.Workers;

/// <summary>
/// Mantiene una conexión WebSocket persistente a Twitch EventSub.
/// Solo inicia transcode cuando Twitch confirma stream.online.
/// Comprueba el estado actual del canal justo después de suscribirse,
/// por si el directo ya estaba activo cuando arrancó la app.
/// </summary>
public sealed class TwitchEventSubWorker : BackgroundService
{
    private const string WssEndpoint = "wss://eventsub.wss.twitch.tv/ws";

    private readonly ILogger<TwitchEventSubWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitorOptions _options;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TwitchEventSubWorker(
        ILogger<TwitchEventSubWorker> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<MonitorOptions> options)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TwitchEventSubWorker iniciado.");

        // Backoff exponencial para reconexiones: 2s, 4s, 8s … máximo 60s
        var backoffSeconds = 2;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Si no hay canales Twitch, no tiene sentido conectar el WebSocket
            if (!await HasTwitchChannelsAsync(stoppingToken))
            {
                _logger.LogDebug("Sin canales Twitch. TwitchEventSubWorker en espera 60s...");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                continue;
            }

            try
            {
                await RunSessionAsync(WssEndpoint, stoppingToken);
                // Si RunSessionAsync sale limpiamente (shutdown), salir del loop
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Conexión EventSub perdida. Reconectando en {Backoff}s...", backoffSeconds);
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                backoffSeconds = Math.Min(backoffSeconds * 2, 60);
            }
        }

        _logger.LogInformation("TwitchEventSubWorker detenido.");
    }

    /// <summary>
    /// Gestiona una sesión completa: conexión, suscripción y recepción de mensajes.
    /// Sale si recibe session_reconnect (relanza con la nueva URL) o si stoppingToken
    /// es cancelado.
    /// </summary>
    private async Task RunSessionAsync(string wsUrl, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        _logger.LogInformation("EventSub WebSocket conectado a {Url}", wsUrl);

        // Buffer de 64KB — suficiente para cualquier mensaje de EventSub
        var buffer = new byte[65536];
        var messageBuilder = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            messageBuilder.Clear();
            WebSocketReceiveResult result;

            // Leer el mensaje completo (puede llegar en múltiples frames)
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("EventSub WS cerrado por el servidor: {CloseStatus}", result.CloseStatusDescription);
                    return;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            var raw = messageBuilder.ToString();
            var envelope = JsonSerializer.Deserialize<EventSubEnvelope>(raw, JsonOpts);
            if (envelope is null) continue;

            var reconnectUrl = await HandleMessageAsync(envelope, ct);
            if (reconnectUrl is not null)
            {
                // Twitch pide reconectar a una URL diferente (rolling update del servidor)
                _logger.LogInformation("Reconectando EventSub a {Url}", reconnectUrl);
                _ = ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None);
                await RunSessionAsync(reconnectUrl, ct);
                return;
            }
        }
    }

    /// <summary>
    /// Procesa un mensaje del WebSocket.
    /// Devuelve la URL de reconexión si el mensaje es session_reconnect, null en otro caso.
    /// </summary>
    private async Task<string?> HandleMessageAsync(EventSubEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Metadata.MessageType)
        {
            case "session_welcome":
                var sessionId = envelope.Payload.Session?.Id
                    ?? throw new InvalidOperationException("session_welcome sin session.id");
                var timeoutSeconds = envelope.Payload.Session?.KeepaliveTimeoutSeconds ?? _options.KeepaliveTimeoutSeconds;
                _logger.LogInformation("EventSub sesión establecida. ID={SessionId}, keepalive={Timeout}s",
                    sessionId, timeoutSeconds);
                await SubscribeAllChannelsAsync(sessionId, ct);
                break;

            case "session_keepalive":
                _logger.LogDebug("EventSub keepalive recibido.");
                break;

            case "session_reconnect":
                return envelope.Payload.Session?.ReconnectUrl;

            case "notification":
                await HandleNotificationAsync(envelope, ct);
                break;

            case "revocation":
                _logger.LogWarning("Suscripción revocada: {Type} — Razón: {Status}",
                    envelope.Payload.Subscription?.Type,
                    envelope.Metadata.MessageType);
                break;

            default:
                _logger.LogDebug("Mensaje EventSub desconocido: {Type}", envelope.Metadata.MessageType);
                break;
        }

        return null;
    }

    /// <summary>
    /// Procesa los eventos stream.online y stream.offline.
    /// </summary>
    private async Task HandleNotificationAsync(EventSubEnvelope envelope, CancellationToken ct)
    {
        var eventType    = envelope.Payload.Subscription?.Type;
        var channelLogin = envelope.Payload.Event?.BroadcasterUserLogin;

        if (string.IsNullOrWhiteSpace(channelLogin)) return;

        using var scope        = _scopeFactory.CreateAsyncScope();
        var liveTranscode      = scope.ServiceProvider.GetRequiredService<ILiveTranscodeService>();

        switch (eventType)
        {
            case "stream.online":
                _logger.LogInformation("'{Channel}' está en DIRECTO (EventSub). Iniciando transcode.", channelLogin);
                if (!liveTranscode.IsTranscoding(channelLogin))
                    await liveTranscode.StartAsync(channelLogin, ct: ct);
                break;

            case "stream.offline":
                _logger.LogInformation("'{Channel}' ha terminado el directo (EventSub). Deteniendo transcode.", channelLogin);
                await liveTranscode.StopAsync(channelLogin);
                break;

            default:
                _logger.LogDebug("Notificación no manejada: {EventType}", eventType);
                break;
        }
    }

    /// <summary>
    /// Para cada canal registrado, resuelve su broadcaster_user_id, suscribe
    /// a stream.online/offline y arranca el transcode si el canal ya estaba online.
    /// EventSub solo notifica eventos futuros; si el canal estaba en directo antes
    /// de conectar, hay que comprobarlo explícitamente aquí.
    /// </summary>
    private async Task SubscribeAllChannelsAsync(string sessionId, CancellationToken ct)
    {
        using var scope        = _scopeFactory.CreateAsyncScope();
        var channelRepo        = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var twitchApi          = scope.ServiceProvider.GetRequiredService<ITwitchApiService>();
        var liveTranscode      = scope.ServiceProvider.GetRequiredService<ILiveTranscodeService>();

        var channels = await channelRepo.GetAllAsync(ct);
        var monitored = channels.Where(c => c.IsMonitored).ToList();

        if (monitored.Count == 0)
        {
            _logger.LogDebug("No hay canales monitorizados para suscribir en EventSub.");
            return;
        }

        foreach (var channel in monitored)
        {
            ct.ThrowIfCancellationRequested();

            // Los canales Kick los gestiona TwitchMonitorWorker (polling), no EventSub
            if (channel.Name.StartsWith("kick:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("'{Channel}' es Kick, se omite en EventSub.", channel.Name);
                continue;
            }

            try
            {
                // Para canales con prefijo "twitch:", usar solo la parte del nombre
                var twitchLogin = channel.Name.StartsWith("twitch:", StringComparison.OrdinalIgnoreCase)
                    ? channel.Name["twitch:".Length..]
                    : channel.Name;

                var broadcasterId = await twitchApi.GetUserIdAsync(twitchLogin, ct);
                if (broadcasterId is null)
                {
                    _logger.LogWarning("No se encontró user_id para '{Channel}'. Saltando.", channel.Name);
                    continue;
                }

                await twitchApi.SubscribeToStreamEventsAsync(sessionId, broadcasterId, "stream.online",  ct);
                await twitchApi.SubscribeToStreamEventsAsync(sessionId, broadcasterId, "stream.offline", ct);

                _logger.LogInformation(
                    "Suscrito a stream.online/offline para '{Channel}' (id={Id})",
                    channel.Name, broadcasterId);

                // EventSub solo notifica eventos futuros. Si el canal ya estaba
                // online cuando arrancamos, no recibiremos stream.online.
                // Comprobamos el estado actual y arrancamos el transcode si es necesario.
                var isOnline = await twitchApi.IsChannelLiveAsync(channel.Name, ct);
                if (isOnline && !liveTranscode.IsTranscoding(channel.Name))
                {
                    _logger.LogInformation(
                        "'{Channel}' ya estaba en directo al arrancar. Iniciando transcode.",
                        channel.Name);
                    await liveTranscode.StartAsync(channel.Name, ct: ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error suscribiendo EventSub para '{Channel}'. Reintentando en el próximo ciclo.", channel.Name);
            }
        }
    }

    /// <summary>
    /// Devuelve true si hay al menos un canal Twitch (sin prefijo o con "twitch:") monitorizado.
    /// </summary>
    private async Task<bool> HasTwitchChannelsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateAsyncScope();
        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var channels = await channelRepo.GetAllAsync(ct);
        return channels.Any(c => c.IsMonitored &&
                                 !c.Name.StartsWith("kick:", StringComparison.OrdinalIgnoreCase));
    }
}
