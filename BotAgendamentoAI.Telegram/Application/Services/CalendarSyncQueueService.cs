using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class CalendarSyncQueueService
{
    public const string UpsertAction = "upsert";
    public const string CancelAction = "cancel";
    public const string PendingStatus = "pending";
    public const string ProcessingStatus = "processing";
    public const string DoneStatus = "done";
    public const string FailedStatus = "failed";

    private readonly ILogger<CalendarSyncQueueService> _logger;

    public CalendarSyncQueueService(ILogger<CalendarSyncQueueService> logger)
    {
        _logger = logger;
    }

    public Task EnqueueUpsertAsync(
        BotDbContext db,
        Job job,
        string reason,
        CancellationToken cancellationToken)
    {
        return EnqueueAsync(db, job, UpsertAction, reason, cancellationToken);
    }

    public Task EnqueueCancelAsync(
        BotDbContext db,
        Job job,
        string reason,
        CancellationToken cancellationToken)
    {
        return EnqueueAsync(db, job, CancelAction, reason, cancellationToken);
    }

    private async Task EnqueueAsync(
        BotDbContext db,
        Job job,
        string action,
        string reason,
        CancellationToken cancellationToken)
    {
        if (job.Id <= 0)
        {
            return;
        }

        var safeAction = NormalizeAction(action);
        var now = DateTimeOffset.UtcNow;

        if (safeAction == CancelAction)
        {
            // Cancel wins over pending upsert entries for the same job.
            var staleUpserts = await db.CalendarSyncQueue
                .Where(x =>
                    x.TenantId == job.TenantId
                    && x.JobId == job.Id
                    && x.Action == UpsertAction
                    && x.Status == PendingStatus)
                .ToListAsync(cancellationToken);

            if (staleUpserts.Count > 0)
            {
                db.CalendarSyncQueue.RemoveRange(staleUpserts);
            }
        }

        var existing = await db.CalendarSyncQueue
            .Where(x =>
                x.TenantId == job.TenantId
                && x.JobId == job.Id
                && x.Action == safeAction
                && (x.Status == PendingStatus || x.Status == ProcessingStatus))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            existing.AvailableAtUtc = now;
            existing.UpdatedAtUtc = now;
            existing.LastError = TrimReason(reason);
        }
        else
        {
            db.CalendarSyncQueue.Add(new CalendarSyncQueueItem
            {
                TenantId = string.IsNullOrWhiteSpace(job.TenantId) ? "A" : job.TenantId.Trim(),
                JobId = job.Id,
                Action = safeAction,
                Status = PendingStatus,
                Attempts = 0,
                AvailableAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastError = TrimReason(reason)
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Calendar sync enqueued. tenant={Tenant} jobId={JobId} action={Action} reason={Reason}",
            job.TenantId,
            job.Id,
            safeAction,
            reason);
    }

    private static string NormalizeAction(string action)
    {
        return string.Equals(action, CancelAction, StringComparison.OrdinalIgnoreCase)
            ? CancelAction
            : UpsertAction;
    }

    private static string TrimReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var safe = value.Trim();
        return safe.Length <= 1024 ? safe : safe[..1024];
    }
}
