using BotAgendamentoAI.Telegram.Domain.Enums;

namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class MessageLog
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public long TelegramUserId { get; set; }
    public MessageDirection Direction { get; set; }
    public MessageType MessageType { get; set; }
    public string Text { get; set; } = string.Empty;
    public long? TelegramMessageId { get; set; }
    public long? RelatedJobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
