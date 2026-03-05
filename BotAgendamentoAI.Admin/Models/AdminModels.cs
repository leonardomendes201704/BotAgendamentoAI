namespace BotAgendamentoAI.Admin.Models;

public sealed class AdminOptions
{
    public string? DatabasePath { get; set; }
}

public sealed class DashboardViewModel
{
    public string TenantId { get; set; } = "A";
    public int Days { get; set; } = 30;
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    public int TotalIncomingConversations { get; set; }
    public int TotalMessages { get; set; }
    public int CreatedBookings { get; set; }
    public int HumanHandoffOpen { get; set; }
    public int ConvertedPhones { get; set; }
    public decimal ConversionRatePercent { get; set; }
    public IReadOnlyList<ConversationThreadSummary> RecentConversations { get; set; } = Array.Empty<ConversationThreadSummary>();
    public IReadOnlyList<BookingListItem> RecentBookings { get; set; } = Array.Empty<BookingListItem>();
    public IReadOnlyList<DashboardMapPinItem> MapPins { get; set; } = Array.Empty<DashboardMapPinItem>();
    public IReadOnlyList<string> Tenants { get; set; } = Array.Empty<string>();
}

public sealed class DashboardMapPinItem
{
    public string BookingId { get; set; } = string.Empty;
    public string TenantId { get; set; } = "A";
    public string ServiceCategory { get; set; } = string.Empty;
    public string ServiceTitle { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DateTime StartLocal { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed class ConversationThreadSummary
{
    public string Phone { get; set; } = string.Empty;
    public DateTimeOffset LastMessageAtUtc { get; set; }
    public string LastMessagePreview { get; set; } = string.Empty;
    public string MenuContext { get; set; } = string.Empty;
    public bool IsInHumanHandoff { get; set; }
}

public sealed class ConversationMessageItem
{
    public long Id { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ConversationDetailsViewModel
{
    public string TenantId { get; set; } = "A";
    public string Phone { get; set; } = string.Empty;
    public IReadOnlyList<ConversationMessageItem> Messages { get; set; } = Array.Empty<ConversationMessageItem>();
}

public sealed class BookingListItem
{
    public string Id { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty;
    public string ServiceTitle { get; set; } = string.Empty;
    public DateTime StartLocal { get; set; }
    public int DurationMinutes { get; set; }
    public string Address { get; set; } = string.Empty;
    public string TechnicianName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class BookingsPageViewModel
{
    public string TenantId { get; set; } = "A";
    public IReadOnlyList<BookingListItem> Bookings { get; set; } = Array.Empty<BookingListItem>();
}

public sealed class CategoryItem
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CategoriesPageViewModel
{
    public string TenantId { get; set; } = "A";
    public IReadOnlyList<CategoryItem> Categories { get; set; } = Array.Empty<CategoryItem>();
    public IReadOnlyList<string> Tenants { get; set; } = Array.Empty<string>();
}

public sealed class CategoryEditViewModel
{
    public long? Id { get; set; }
    public string TenantId { get; set; } = "A";
    public string Name { get; set; } = string.Empty;
}

public sealed class ServiceCatalogItem
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public int DefaultDurationMinutes { get; set; } = 60;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ServicesPageViewModel
{
    public string TenantId { get; set; } = "A";
    public IReadOnlyList<ServiceCatalogItem> Services { get; set; } = Array.Empty<ServiceCatalogItem>();
    public IReadOnlyList<string> Tenants { get; set; } = Array.Empty<string>();
}

public sealed class ServiceEditViewModel
{
    public long? Id { get; set; }
    public string TenantId { get; set; } = "A";
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public int DefaultDurationMinutes { get; set; } = 60;
    public bool IsActive { get; set; } = true;
    public IReadOnlyList<string> AvailableCategories { get; set; } = Array.Empty<string>();
}

public sealed class BotConfigViewModel
{
    public string TenantId { get; set; } = "A";
    public string MainMenuText { get; set; } = string.Empty;
    public string GreetingText { get; set; } = string.Empty;
    public string HumanHandoffText { get; set; } = string.Empty;
    public string ClosingText { get; set; } = string.Empty;
    public string FallbackText { get; set; } = string.Empty;
    public int MessagePoolingSeconds { get; set; } = 15;
    public string TelegramBotId { get; set; } = string.Empty;
    public string TelegramBotUsername { get; set; } = string.Empty;
    public string TelegramBotToken { get; set; } = string.Empty;
    public bool TelegramIsActive { get; set; }
    public int TelegramPollingTimeoutSeconds { get; set; } = 30;
    public long TelegramLastUpdateId { get; set; }
    public IReadOnlyList<string> Tenants { get; set; } = Array.Empty<string>();
}
