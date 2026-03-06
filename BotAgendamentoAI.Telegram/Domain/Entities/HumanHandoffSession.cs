namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class HumanHandoffSession
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public long TelegramUserId { get; set; }
    public long? AppUserId { get; set; }
    public string RequestedByRole { get; set; } = "unknown";
    public bool IsOpen { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public string? AssignedAgent { get; set; }
    public string? PreviousState { get; set; }
    public string? CloseReason { get; set; }
    public DateTimeOffset LastMessageAtUtc { get; set; }
}
