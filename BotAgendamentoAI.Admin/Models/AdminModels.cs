namespace BotAgendamentoAI.Admin.Models;

public sealed class AdminOptions
{
    public string? DatabasePath { get; set; }
    public string? ConnectionString { get; set; }
    public int DashboardRealtimePollSeconds { get; set; } = 2;
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
    public int FinishedBookings { get; set; }
    public int CancelledBookings { get; set; }
    public int RejectedJobs { get; set; }
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
    public string LastMessageDirection { get; set; } = string.Empty;
    public bool IsAwaitingHumanReply { get; set; }
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
    public ConversationHandoffStatus Handoff { get; set; } = new();
    public IReadOnlyList<ConversationMessageItem> Messages { get; set; } = Array.Empty<ConversationMessageItem>();
}

public sealed class ConversationOrderClientContext
{
    public string TenantId { get; set; } = "A";
    public string Phone { get; set; } = string.Empty;
    public bool IsTelegramThread { get; set; }
    public bool UserExists { get; set; }
    public bool IsClientEligible { get; set; }
    public bool IsRegistrationComplete { get; set; }
    public string UserRole { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string Cep { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string Complement { get; set; } = string.Empty;
    public string Neighborhood { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public sealed class ConversationOrderDraftCommand
{
    public string TenantId { get; set; } = "A";
    public string Phone { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AddressText { get; set; } = string.Empty;
    public string Cep { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsUrgent { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string PreferenceCode { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
}

public sealed class ConversationOrderDraftResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public long? TelegramMessageId { get; set; }
    public ConversationHandoffStatus Handoff { get; set; } = new();
}

public sealed class ConversationHandoffStatus
{
    public string TenantId { get; set; } = "A";
    public string Phone { get; set; } = string.Empty;
    public bool IsTelegramThread { get; set; }
    public bool IsOpen { get; set; }
    public string RequestedByRole { get; set; } = string.Empty;
    public string AssignedAgent { get; set; } = string.Empty;
    public string PreviousState { get; set; } = string.Empty;
    public string CloseReason { get; set; } = string.Empty;
    public DateTimeOffset? RequestedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public DateTimeOffset? LastMessageAtUtc { get; set; }
}

public sealed class SendHumanMessageResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public long? TelegramMessageId { get; set; }
    public ConversationHandoffStatus Handoff { get; set; } = new();
}

public sealed class BookingListItem
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty;
    public string ServiceTitle { get; set; } = string.Empty;
    public DateTime StartLocal { get; set; }
    public int DurationMinutes { get; set; }
    public string Address { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string TechnicianName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class BookingsPageViewModel
{
    public string TenantId { get; set; } = "A";
    public IReadOnlyList<BookingListItem> Bookings { get; set; } = Array.Empty<BookingListItem>();
}

public sealed class ClientListItem
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public long TelegramUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
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
    public bool IsRegistrationComplete { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int TotalJobs { get; set; }
    public int OpenJobs { get; set; }
    public int FinishedJobs { get; set; }
    public int CancelledJobs { get; set; }
    public DateTimeOffset? LastJobAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public string AddressSummary
    {
        get
        {
            var parts = new List<string>();
            var firstLine = new List<string>();

            if (!string.IsNullOrWhiteSpace(Street))
            {
                firstLine.Add(Street.Trim());
            }

            if (!string.IsNullOrWhiteSpace(Number))
            {
                firstLine.Add(Number.Trim());
            }

            if (!string.IsNullOrWhiteSpace(Complement))
            {
                firstLine.Add(Complement.Trim());
            }

            if (firstLine.Count > 0)
            {
                parts.Add(string.Join(", ", firstLine));
            }

            if (!string.IsNullOrWhiteSpace(Neighborhood))
            {
                parts.Add(Neighborhood.Trim());
            }

            var cityUf = string.Join(" - ", new[] { City?.Trim() ?? string.Empty, State?.Trim() ?? string.Empty }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(cityUf))
            {
                parts.Add(cityUf);
            }

            if (!string.IsNullOrWhiteSpace(Cep))
            {
                parts.Add($"CEP {Cep}");
            }

            return string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
    }
}

public sealed class ClientsPageViewModel
{
    public string TenantId { get; set; } = "A";
    public IReadOnlyList<ClientListItem> Clients { get; set; } = Array.Empty<ClientListItem>();
    public IReadOnlyList<string> Tenants { get; set; } = Array.Empty<string>();
}

public sealed class ProviderListItem
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "A";
    public long TelegramUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsAvailable { get; set; }
    public string CategoriesSummary { get; set; } = string.Empty;
    public int RadiusKm { get; set; }
    public decimal AvgRating { get; set; }
    public int TotalReviews { get; set; }
    public double? BaseLatitude { get; set; }
    public double? BaseLongitude { get; set; }
    public int TotalJobs { get; set; }
    public int OpenJobs { get; set; }
    public int FinishedJobs { get; set; }
    public int CancelledJobs { get; set; }
    public DateTimeOffset? LastJobAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ProvidersPageViewModel
{
    public string TenantId { get; set; } = "A";
    public IReadOnlyList<ProviderListItem> Providers { get; set; } = Array.Empty<ProviderListItem>();
    public IReadOnlyList<string> Tenants { get; set; } = Array.Empty<string>();
}

public sealed class ProviderCoverageItem
{
    public long ProviderUserId { get; set; }
    public long TelegramUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public int RadiusKm { get; set; } = 10;
    public double? BaseLatitude { get; set; }
    public double? BaseLongitude { get; set; }
    public IReadOnlyList<string> Neighborhoods { get; set; } = Array.Empty<string>();
}

public sealed class CoveragePageViewModel
{
    public string TenantId { get; set; } = "A";
    public IReadOnlyList<string> Tenants { get; set; } = Array.Empty<string>();
    public IReadOnlyList<ProviderCoverageItem> Providers { get; set; } = Array.Empty<ProviderCoverageItem>();
    public IReadOnlyList<string> Neighborhoods { get; set; } = Array.Empty<string>();
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
    public bool HasOpenAiApiKey { get; set; }
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string MainMenuText { get; set; } = string.Empty;
    public string GreetingText { get; set; } = string.Empty;
    public string HumanHandoffText { get; set; } = string.Empty;
    public string CloseConfirmationText { get; set; } = string.Empty;
    public string ClosingText { get; set; } = string.Empty;
    public string FallbackText { get; set; } = string.Empty;
    public int MessagePoolingSeconds { get; set; } = 15;
    public string TelegramBotId { get; set; } = string.Empty;
    public string TelegramBotUsername { get; set; } = string.Empty;
    public string TelegramBotToken { get; set; } = string.Empty;
    public bool TelegramIsActive { get; set; }
    public int TelegramPollingTimeoutSeconds { get; set; } = 30;
    public long TelegramLastUpdateId { get; set; }
    public bool ProviderReminderEnabled { get; set; } = true;
    public int ProviderReminderSweepIntervalMinutes { get; set; } = 5;
    public int ProviderReminderResendCooldownMinutes { get; set; } = 5;
    public int ProviderReminderSnoozeHours { get; set; } = 24;
    public bool GoogleCalendarEnabled { get; set; }
    public string GoogleCalendarId { get; set; } = string.Empty;
    public string GoogleCalendarServiceAccountJson { get; set; } = string.Empty;
    public bool HasGoogleCalendarServiceAccountJson { get; set; }
    public string GoogleCalendarTimeZoneId { get; set; } = "America/Sao_Paulo";
    public int GoogleCalendarDefaultDurationMinutes { get; set; } = 60;
    public int GoogleCalendarAvailabilityWindowDays { get; set; } = 7;
    public int GoogleCalendarAvailabilitySlotIntervalMinutes { get; set; } = 60;
    public int GoogleCalendarAvailabilityWorkdayStartHour { get; set; } = 8;
    public int GoogleCalendarAvailabilityWorkdayEndHour { get; set; } = 20;
    public int GoogleCalendarAvailabilityTodayLeadMinutes { get; set; } = 30;
    public int GoogleCalendarMaxAttempts { get; set; } = 8;
    public int GoogleCalendarRetryBaseSeconds { get; set; } = 10;
    public int GoogleCalendarRetryMaxSeconds { get; set; } = 600;
    public string GoogleCalendarEventTitleTemplate { get; set; } = string.Empty;
    public string GoogleCalendarEventDescriptionTemplate { get; set; } = string.Empty;
    public IReadOnlyList<TelegramUserOption> TelegramUsers { get; set; } = Array.Empty<TelegramUserOption>();
    public IReadOnlyList<string> Tenants { get; set; } = Array.Empty<string>();
}

public sealed class TelegramUserOption
{
    public long TelegramUserId { get; set; }
    public string DisplayLabel { get; set; } = string.Empty;
}

public sealed class TelegramMemoryResetResult
{
    public bool FoundUser { get; set; }
    public int SessionsReset { get; set; }
    public int TelegramMessagesDeleted { get; set; }
    public int LegacyConversationMessagesDeleted { get; set; }
    public int LegacyConversationStateDeleted { get; set; }
}

public sealed class TenantOperationalResetResult
{
    public int LegacyConversationMessagesDeleted { get; set; }
    public int LegacyConversationStateDeleted { get; set; }
    public int LegacyBookingsDeleted { get; set; }
    public int LegacyBookingGeocodeCacheDeleted { get; set; }
    public int TelegramJobPhotosDeleted { get; set; }
    public int TelegramRatingsDeleted { get; set; }
    public int TelegramMessagesDeleted { get; set; }
    public int TelegramJobsDeleted { get; set; }
    public int TelegramProviderPortfolioDeleted { get; set; }
    public int TelegramUserSessionsDeleted { get; set; }
    public int TelegramProviderProfilesDeleted { get; set; }
    public int TelegramUsersDeleted { get; set; }
}
