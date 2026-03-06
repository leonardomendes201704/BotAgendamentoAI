namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class ProviderJobRejection
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public long JobId { get; set; }
    public long ProviderUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
