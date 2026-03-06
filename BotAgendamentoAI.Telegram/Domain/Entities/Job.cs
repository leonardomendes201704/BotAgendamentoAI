using BotAgendamentoAI.Telegram.Domain.Enums;

namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class Job
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public long ClientUserId { get; set; }
    public long? ProviderUserId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Draft;
    public DateTimeOffset? ScheduledAt { get; set; }
    public bool IsUrgent { get; set; }
    public string? AddressText { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string PreferenceCode { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public decimal? FinalAmount { get; set; }
    public string? FinalNotes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AppUser ClientUser { get; set; } = null!;
    public AppUser? ProviderUser { get; set; }
    public ICollection<JobPhoto> Photos { get; set; } = new List<JobPhoto>();
    public ICollection<Rating> Ratings { get; set; } = new List<Rating>();
}
