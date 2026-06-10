namespace Sepius.Domain.Entities;

public class ChatMessage
{
    public int Id { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}
