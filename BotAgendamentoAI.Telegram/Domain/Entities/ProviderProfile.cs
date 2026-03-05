namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class ProviderProfile
{
    public long UserId { get; set; }
    public string Bio { get; set; } = string.Empty;
    public string CategoriesJson { get; set; } = "[]";
    public int RadiusKm { get; set; } = 10;
    public decimal AvgRating { get; set; }
    public int TotalReviews { get; set; }
    public bool IsAvailable { get; set; } = true;
    public double? BaseLatitude { get; set; }
    public double? BaseLongitude { get; set; }

    public AppUser User { get; set; } = null!;
    public ICollection<ProviderPortfolioPhoto> PortfolioPhotos { get; set; } = new List<ProviderPortfolioPhoto>();
}
