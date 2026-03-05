namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class ServiceCategoryEntity
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
