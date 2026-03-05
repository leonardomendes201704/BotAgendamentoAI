namespace BotAgendamentoAI.Telegram.Domain.Fsm;

public sealed class UserDraft
{
    public string? Category { get; set; }
    public string? Description { get; set; }
    public List<string> PhotoFileIds { get; set; } = new();
    public string? AddressText { get; set; }
    public string? Cep { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsUrgent { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? PreferenceCode { get; set; }
    public decimal? FinalAmount { get; set; }
    public string? FinalNotes { get; set; }
    public List<string> AfterPhotoFileIds { get; set; } = new();

    public static UserDraft Empty() => new();
}
