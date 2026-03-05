namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class UserSession
{
    public long UserId { get; set; }
    public string State { get; set; } = "NONE";
    public string DraftJson { get; set; } = "{}";
    public long? ActiveJobId { get; set; }
    public long? ChatJobId { get; set; }
    public long? ChatPeerUserId { get; set; }
    public bool IsChatActive { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AppUser User { get; set; } = null!;
}
