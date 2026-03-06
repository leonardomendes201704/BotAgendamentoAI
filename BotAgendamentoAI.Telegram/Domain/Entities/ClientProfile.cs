namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class ClientProfile
{
    public long UserId { get; set; }
    public string TenantId { get; set; } = "A";
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string Complement { get; set; } = string.Empty;
    public string Neighborhood { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Cep { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsAddressConfirmed { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public bool IsRegistrationComplete { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public AppUser User { get; set; } = null!;
}
