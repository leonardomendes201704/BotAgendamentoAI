using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Infrastructure.Services;

public sealed class TenantConfigService
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;

    public TenantConfigService(IDbContextFactory<BotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<TelegramTenantConfig>> GetActiveConfigsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TelegramConfigs
            .AsNoTracking()
            .Where(x => x.IsActive && x.BotToken != null && x.BotToken != "")
            .OrderBy(x => x.TenantId)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateLastUpdateIdAsync(string tenantId, long lastUpdateId, CancellationToken cancellationToken)
    {
        var safeTenant = string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.TelegramConfigs.FirstOrDefaultAsync(x => x.TenantId == safeTenant, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.LastUpdateId = Math.Max(lastUpdateId, 0L);
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
