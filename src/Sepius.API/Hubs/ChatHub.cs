using Microsoft.AspNetCore.SignalR;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.Persistence;

namespace Sepius.API.Hubs;

public class ChatHub(AppDbContext db, ILogger<ChatHub> logger) : Hub
{
    private const int MaxReplyPreview = 120;

    public Task SendMessage(string nickname, string text) => Send(nickname, text, null);

    /// <summary>Responde a un mensaje existente. El autor y el texto citados se leen de la base de datos, no del cliente.</summary>
    public Task SendReply(string nickname, string text, int replyToId) => Send(nickname, text, replyToId);

    private async Task Send(string nickname, string text, int? replyToId)
    {
        if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(text))
            return;

        nickname = nickname.Trim()[..Math.Min(nickname.Trim().Length, 32)];
        text = text.Trim()[..Math.Min(text.Trim().Length, 500)];

        var message = new ChatMessage { Nickname = nickname, Text = text };

        if (replyToId is > 0)
        {
            try
            {
                var original = await db.ChatMessages.FindAsync(replyToId.Value);
                if (original is not null)
                {
                    message.ReplyToId = original.Id;
                    message.ReplyToNickname = original.Nickname;
                    message.ReplyToText = original.Text.Length > MaxReplyPreview
                        ? original.Text[..MaxReplyPreview] + "…"
                        : original.Text;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error buscando el mensaje citado ({ReplyToId})", replyToId);
            }
        }

        try
        {
            db.ChatMessages.Add(message);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error guardando mensaje de chat en DB");
        }

        object? replyTo = message.ReplyToId is null
            ? null
            : new { id = message.ReplyToId, nickname = message.ReplyToNickname, text = message.ReplyToText };

        // Los dos primeros parámetros siguen siendo (nickname, text, sentAt) para los clientes antiguos;
        // id y replyTo se añaden al final.
        await Clients.All.SendAsync("ReceiveMessage", nickname, text, DateTime.UtcNow, message.Id, replyTo);
    }

    public async Task GetHistory()
    {
        try
        {
            var history = db.ChatMessages
                .OrderByDescending(m => m.SentAt)
                .Take(50)
                .OrderBy(m => m.SentAt)
                .Select(m => new
                {
                    m.Id,
                    m.Nickname,
                    m.Text,
                    m.SentAt,
                    ReplyTo = m.ReplyToId == null
                        ? null
                        : new { Id = m.ReplyToId, Nickname = m.ReplyToNickname, Text = m.ReplyToText }
                })
                .ToList();

            await Clients.Caller.SendAsync("History", history);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error cargando historial de chat desde DB");
            await Clients.Caller.SendAsync("History", Array.Empty<object>());
        }
    }
}
