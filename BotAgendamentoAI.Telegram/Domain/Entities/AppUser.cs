using BotAgendamentoAI.Telegram.Domain.Enums;

namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class AppUser
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public long TelegramUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public UserRole Role { get; set; } = UserRole.Client;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ClientProfile? ClientProfile { get; set; }
    public ProviderProfile? ProviderProfile { get; set; }
    public UserSession? Session { get; set; }
    public ICollection<Job> ClientJobs { get; set; } = new List<Job>();
    public ICollection<Job> ProviderJobs { get; set; } = new List<Job>();
}
