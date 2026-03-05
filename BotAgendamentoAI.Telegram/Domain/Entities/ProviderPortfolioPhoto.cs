namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class ProviderPortfolioPhoto
{
    public long Id { get; set; }
    public long ProviderUserId { get; set; }
    public string FileIdOrUrl { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public AppUser ProviderUser { get; set; } = null!;
}
