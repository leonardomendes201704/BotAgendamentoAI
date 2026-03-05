namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class Rating
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public long ClientUserId { get; set; }
    public long ProviderUserId { get; set; }
    public int Stars { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Job Job { get; set; } = null!;
}
