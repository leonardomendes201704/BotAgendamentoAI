namespace BotAgendamentoAI.Admin.Realtime;

public interface IDashboardRealtimeNotifier
{
    Task NotifyTenantChangedAsync(string tenantId, CancellationToken cancellationToken = default);
}
