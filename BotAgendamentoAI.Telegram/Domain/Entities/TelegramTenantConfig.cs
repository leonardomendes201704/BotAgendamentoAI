namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class TelegramTenantConfig
{
    public string TenantId { get; set; } = "A";
    public string BotId { get; set; } = string.Empty;
    public string BotUsername { get; set; } = string.Empty;
    public string BotToken { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int PollingTimeoutSeconds { get; set; } = 30;
    public long LastUpdateId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
