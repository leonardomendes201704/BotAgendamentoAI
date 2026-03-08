namespace BotAgendamentoAI.Telegram.Domain.Fsm;

public sealed class UserDraft
{
    public string? Category { get; set; }
    public string? Description { get; set; }
    public List<string> PhotoFileIds { get; set; } = new();
    public string? AddressText { get; set; }
    public string? Cep { get; set; }
    public string? AddressBaseFromCep { get; set; }
    public string? AddressNumber { get; set; }
    public string? AddressComplement { get; set; }
    public bool WaitingAddressNumber { get; set; }
    public bool WaitingAddressComplementChoice { get; set; }
    public bool WaitingAddressComplement { get; set; }
    public bool WaitingAddressConfirmation { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsUrgent { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? PreferenceCode { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public decimal? FinalAmount { get; set; }
    public string? FinalNotes { get; set; }
    public List<string> AfterPhotoFileIds { get; set; } = new();
    public List<long> HiddenFeedJobIds { get; set; } = new();
    public List<string> ProviderCategoryNames { get; set; } = new();
    public DateTimeOffset? ProviderProfileReminderLastSentUtc { get; set; }
    public DateTimeOffset? ProviderProfileReminderSnoozeUntilUtc { get; set; }

    public static UserDraft Empty() => new();
}
