using BotAgendamentoAI.Admin.Models;

namespace BotAgendamentoAI.Admin.Data;

public interface IAdminRepository
{
    Task InitializeAsync();
    Task<IReadOnlyList<string>> GetTenantIdsAsync();
    Task<DashboardViewModel> GetDashboardAsync(string tenantId, int days);
    Task<IReadOnlyList<DashboardMapPinItem>> GetDashboardMapPinsAsync(string tenantId, DateTimeOffset? sinceUtc, int limit);
    Task<IReadOnlyList<ConversationThreadSummary>> GetConversationThreadsAsync(string tenantId, int limit);
    Task<IReadOnlyList<ConversationMessageItem>> GetConversationMessagesAsync(string tenantId, string phone, int limit);
    Task<ConversationHandoffStatus> GetConversationHandoffStatusAsync(string tenantId, string phone);
    Task<ConversationHandoffStatus> OpenConversationHandoffAsync(string tenantId, string phone, string? agent);
    Task<ConversationHandoffStatus> CloseConversationHandoffAsync(string tenantId, string phone, string? agent, string? reason);
    Task<SendHumanMessageResult> SendHumanMessageAsync(string tenantId, string phone, string message, string? agent);
    Task<ConversationOrderClientContext> GetConversationOrderClientContextAsync(string tenantId, string phone);
    Task<ConversationOrderDraftResult> SendClientOrderConfirmationAsync(ConversationOrderDraftCommand input, string? agent);
    Task<IReadOnlyList<BookingListItem>> GetBookingsAsync(string tenantId, int limit);
    Task<IReadOnlyList<ClientListItem>> GetClientsAsync(string tenantId, int limit);
    Task<IReadOnlyList<ProviderListItem>> GetProvidersAsync(string tenantId, int limit);
    Task<IReadOnlyList<ProviderCoverageItem>> GetProviderCoverageAsync(string tenantId, int limit);
    Task<IReadOnlyList<CategoryItem>> GetCategoriesAsync(string tenantId);
    Task<CategoryItem?> GetCategoryByIdAsync(string tenantId, long id);
    Task<CategoryItem> CreateCategoryAsync(string tenantId, string name);
    Task<CategoryItem?> UpdateCategoryAsync(string tenantId, long id, string name);
    Task<bool> DeleteCategoryAsync(string tenantId, long id);
    Task<IReadOnlyList<ServiceCatalogItem>> GetServicesAsync(string tenantId);
    Task<ServiceCatalogItem?> GetServiceByIdAsync(string tenantId, long id);
    Task<ServiceCatalogItem> CreateServiceAsync(ServiceEditViewModel input);
    Task<ServiceCatalogItem?> UpdateServiceAsync(ServiceEditViewModel input);
    Task<bool> DeleteServiceAsync(string tenantId, long id);
    Task<BotConfigViewModel> GetBotConfigAsync(string tenantId);
    Task<IReadOnlyList<TelegramUserOption>> GetTelegramUsersAsync(string tenantId, int limit = 200);
    Task SaveBotConfigAsync(BotConfigViewModel input);
    Task<TelegramMemoryResetResult> ResetTelegramMemoryAsync(string tenantId, long telegramUserId, bool clearHistory);
    Task<TenantOperationalResetResult> ResetTenantOperationalDataAsync(string tenantId);
}
