using Microsoft.AspNetCore.SignalR;

namespace BotAgendamentoAI.Admin.Realtime;

public sealed class DashboardHub : Hub
{
    public Task JoinAll()
        => Groups.AddToGroupAsync(Context.ConnectionId, BuildAllGroup());

    public Task LeaveAll()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildAllGroup());

    public Task JoinTenant(string tenantId)
        => Groups.AddToGroupAsync(Context.ConnectionId, BuildTenantGroup(tenantId));

    public Task LeaveTenant(string tenantId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildTenantGroup(tenantId));

    public static string BuildAllGroup()
        => "dashboard:all";

    public static string BuildTenantGroup(string tenantId)
        => $"dashboard:{NormalizeTenant(tenantId)}";

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();
}
