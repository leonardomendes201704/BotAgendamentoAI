using BotAgendamentoAI.WhatsApp.Models;

namespace BotAgendamentoAI.WhatsApp.Data;

public interface IWhatsAppTenantConfigRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WhatsAppTenantConfig>> GetConfigsAsync(bool activeOnly, CancellationToken cancellationToken = default);
}
