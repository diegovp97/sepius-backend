using Microsoft.AspNetCore.SignalR;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.Persistence;

namespace Sepius.API.Hubs;

public class ChatHub(AppDbContext db, ILogger<ChatHub> logger) : Hub
{
    public async Task SendMessage(string nickname, string text)
    {
        if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(text))
            return;

        nickname = nickname.Trim()[..Math.Min(nickname.Trim().Length, 32)];
        text = text.Trim()[..Math.Min(text.Trim().Length, 500)];

        try
        {
            var message = new ChatMessage { Nickname = nickname, Text = text };
            db.ChatMessages.Add(message);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error guardando mensaje de chat en DB");
        }

        await Clients.All.SendAsync("ReceiveMessage", nickname, text, DateTime.UtcNow);
    }

    public async Task GetHistory()
    {
        try
        {
            var history = db.ChatMessages
                .OrderByDescending(m => m.SentAt)
                .Take(50)
                .OrderBy(m => m.SentAt)
                .Select(m => new { m.Nickname, m.Text, m.SentAt })
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
