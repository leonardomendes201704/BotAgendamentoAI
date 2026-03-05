namespace BotAgendamentoAI.Telegram;

public sealed class TelegramWorkerOptions
{
    public string? DatabasePath { get; set; }
    public string TimeZoneId { get; set; } = "America/Sao_Paulo";
    public int TenantIdleDelaySeconds { get; set; } = 3;
    public int SessionExpiryMinutes { get; set; } = 180;
    public int HistoryLimitPerContext { get; set; } = 20;
    public bool EnablePhotoValidation { get; set; }
}

public sealed class TelegramRuntimeSettings
{
    public string DatabasePath { get; init; } = string.Empty;
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    public int TenantIdleDelaySeconds { get; init; } = 3;
    public int SessionExpiryMinutes { get; init; } = 180;
    public int HistoryLimitPerContext { get; init; } = 20;
    public bool EnablePhotoValidation { get; init; }
}
