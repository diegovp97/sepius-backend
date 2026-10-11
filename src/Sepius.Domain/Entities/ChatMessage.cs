namespace Sepius.Domain.Entities;

public class ChatMessage
{
    public int Id { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    // Respuesta a otro mensaje: se guarda una copia (autor y texto recortado) para no depender del original.
    public int? ReplyToId { get; set; }
    public string? ReplyToNickname { get; set; }
    public string? ReplyToText { get; set; }
}
