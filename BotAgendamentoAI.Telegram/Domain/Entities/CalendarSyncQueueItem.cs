namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class CalendarSyncQueueItem
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public long JobId { get; set; }
    public string Action { get; set; } = "upsert";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
