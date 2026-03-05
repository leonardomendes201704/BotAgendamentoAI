namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class JobPhoto
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public string TelegramFileId { get; set; } = string.Empty;
    public string Kind { get; set; } = "before";
    public DateTimeOffset CreatedAt { get; set; }

    public Job Job { get; set; } = null!;
}
