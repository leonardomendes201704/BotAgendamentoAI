namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class JobCalendarLink
{
    public long JobId { get; set; }
    public string TenantId { get; set; } = "A";
    public string CalendarEventId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
