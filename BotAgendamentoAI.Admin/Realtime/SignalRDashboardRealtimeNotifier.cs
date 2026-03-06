using Microsoft.AspNetCore.SignalR;

namespace BotAgendamentoAI.Admin.Realtime;

public sealed class SignalRDashboardRealtimeNotifier : IDashboardRealtimeNotifier
{
    private readonly IHubContext<DashboardHub> _hubContext;

    public SignalRDashboardRealtimeNotifier(IHubContext<DashboardHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task NotifyTenantChangedAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var normalizedTenant = NormalizeTenant(tenantId);
        var payload = new DashboardChangedEvent(normalizedTenant, DateTimeOffset.UtcNow);
        return Task.WhenAll(
            _hubContext
                .Clients
                .Group(DashboardHub.BuildTenantGroup(normalizedTenant))
                .SendAsync("dashboardChanged", payload, cancellationToken),
            _hubContext
                .Clients
                .Group(DashboardHub.BuildAllGroup())
                .SendAsync("dashboardChanged", payload, cancellationToken));
    }

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();
}

public sealed record DashboardChangedEvent(string TenantId, DateTimeOffset AtUtc);
