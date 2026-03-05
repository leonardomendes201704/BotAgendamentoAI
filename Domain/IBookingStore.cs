namespace BotAgendamentoAI.Domain;

public interface IBookingStore
{
    Booking Create(
        string tenantId,
        string customerPhone,
        string customerName,
        string serviceCategory,
        string serviceTitle,
        DateTime startLocal,
        int durationMinutes,
        string address,
        string notes,
        string technicianName);

    IReadOnlyList<Booking> List(
        string tenantId,
        string? customerPhone = null,
        DateTime? from = null,
        DateTime? to = null);

    bool Cancel(string tenantId, string bookingId);
    Booking? Get(string tenantId, string bookingId);
    Booking? Reschedule(string tenantId, string bookingId, DateTime newStartLocal);
    IReadOnlyList<ServiceCategory> GetCategories(string tenantId);
    ServiceCategory EnsureCategory(string tenantId, string categoryName);
}
