namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class TenantBotConfig
{
    public string TenantId { get; set; } = "A";
    public string MenuJson { get; set; } = "{}";
    public string MessagesJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
