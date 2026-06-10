using Microsoft.AspNetCore.SignalR;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.Persistence;

namespace Sepius.API.Hubs;

public class ChatHub(AppDbContext db) : Hub
{
    public async Task SendMessage(string nickname, string text)
    {
        if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(text))
            return;

        nickname = nickname.Trim()[..Math.Min(nickname.Trim().Length, 32)];
        text = text.Trim()[..Math.Min(text.Trim().Length, 500)];

        var message = new ChatMessage { Nickname = nickname, Text = text };
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();

        await Clients.All.SendAsync("ReceiveMessage", nickname, text, message.SentAt);
    }

    public async Task GetHistory()
    {
        var history = db.ChatMessages
            .OrderByDescending(m => m.SentAt)
            .Take(50)
            .OrderBy(m => m.SentAt)
            .Select(m => new { m.Nickname, m.Text, m.SentAt })
            .ToList();

        await Clients.Caller.SendAsync("History", history);
    }
}
