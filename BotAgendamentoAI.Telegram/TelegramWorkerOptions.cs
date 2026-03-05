namespace BotAgendamentoAI.Telegram;

public sealed class TelegramWorkerOptions
{
    public string? DatabasePath { get; set; }
    public string TimeZoneId { get; set; } = "America/Sao_Paulo";
    public string OpenAiModel { get; set; } = "gpt-4.1-mini";
    public int TenantIdleDelaySeconds { get; set; } = 3;
}

public sealed class TelegramRuntimeSettings
{
    public string DatabasePath { get; init; } = string.Empty;
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    public string OpenAiModel { get; init; } = "gpt-4.1-mini";
    public int TenantIdleDelaySeconds { get; init; } = 3;
}
