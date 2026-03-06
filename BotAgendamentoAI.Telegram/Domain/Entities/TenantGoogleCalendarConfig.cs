namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class TenantGoogleCalendarConfig
{
    public string TenantId { get; set; } = "A";
    public bool IsEnabled { get; set; }
    public string CalendarId { get; set; } = string.Empty;
    public string ServiceAccountJson { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "America/Sao_Paulo";
    public int DefaultDurationMinutes { get; set; } = 60;
    public int MaxAttempts { get; set; } = 8;
    public int RetryBaseSeconds { get; set; } = 10;
    public int RetryMaxSeconds { get; set; } = 600;
    public string EventTitleTemplate { get; set; } = string.Empty;
    public string EventDescriptionTemplate { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
