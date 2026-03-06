using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class AvailabilityService
{
    public async Task<AvailabilityRules> GetRulesAsync(
        BotDbContext db,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var safeTenant = string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();
        var config = await db.TenantGoogleCalendarConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == safeTenant, cancellationToken);
        return AvailabilityRules.FromConfig(config);
    }

    public async Task<IReadOnlyList<DateTime>> GetAvailableDaysAsync(
        BotDbContext db,
        AvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var rules = request.Rules;
        var busy = await LoadBusyJobsAsync(db, request, cancellationToken);
        var startDate = request.NowLocal.Date;
        var output = new List<DateTime>();

        for (var day = 0; day < rules.WindowDays; day++)
        {
            var date = startDate.AddDays(day);
            var slots = BuildDaySlots(request.TimeZone, rules, date, request.NowLocal, request.RequireFutureSlotsOnly && day == 0);
            if (slots.Any(slot => !HasConflict(busy, slot, slot.AddMinutes(rules.DefaultDurationMinutes), rules.DefaultDurationMinutes)))
            {
                output.Add(date);
            }
        }

        return output;
    }

    public async Task<IReadOnlyList<string>> GetAvailableTimeSlotsAsync(
        BotDbContext db,
        AvailabilityRequest request,
        string yyyymmdd,
        CancellationToken cancellationToken)
    {
        if (!TryParseDayToken(yyyymmdd, out var day))
        {
            return Array.Empty<string>();
        }

        var rules = request.Rules;
        var busy = await LoadBusyJobsAsync(db, request, cancellationToken);
        var slots = BuildDaySlots(request.TimeZone, rules, day, request.NowLocal, request.RequireFutureSlotsOnly && day.Date == request.NowLocal.Date)
            .Where(slot => !HasConflict(busy, slot, slot.AddMinutes(rules.DefaultDurationMinutes), rules.DefaultDurationMinutes))
            .OrderBy(slot => slot)
            .Select(slot => slot.ToString("HHmm"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return slots;
    }

    public async Task<AvailabilityCheckResult> CheckSlotAvailabilityAsync(
        BotDbContext db,
        AvailabilityRequest request,
        DateTimeOffset start,
        CancellationToken cancellationToken)
    {
        var rules = request.Rules;
        var end = start.AddMinutes(rules.DefaultDurationMinutes);
        var busy = await LoadBusyJobsAsync(db, request, cancellationToken);
        var conflict = HasConflict(busy, start, end, rules.DefaultDurationMinutes);
        return new AvailabilityCheckResult
        {
            IsAvailable = !conflict,
            Start = start,
            End = end
        };
    }

    public static bool TryParseDayToken(string? token, out DateTime day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var safe = token.Trim();
        if (safe.Length != 8 || !safe.All(char.IsDigit))
        {
            return false;
        }

        if (!int.TryParse(safe[..4], out var year)
            || !int.TryParse(safe.Substring(4, 2), out var month)
            || !int.TryParse(safe.Substring(6, 2), out var date))
        {
            return false;
        }

        try
        {
            day = new DateTime(year, month, date, 0, 0, 0, DateTimeKind.Unspecified);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<Job>> LoadBusyJobsAsync(
        BotDbContext db,
        AvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var safeTenant = string.IsNullOrWhiteSpace(request.TenantId) ? "A" : request.TenantId.Trim();
        var query = db.Jobs
            .AsNoTracking()
            .Where(x =>
                x.TenantId == safeTenant
                && x.ScheduledAt != null
                && (x.ClientUserId == request.ClientUserId
                    || (request.ProviderUserId.HasValue && x.ProviderUserId == request.ProviderUserId.Value))
                && (x.Status == JobStatus.Requested
                    || x.Status == JobStatus.WaitingProvider
                    || x.Status == JobStatus.Accepted
                    || x.Status == JobStatus.OnTheWay
                    || x.Status == JobStatus.Arrived
                    || x.Status == JobStatus.InProgress));

        if (request.ExcludeJobId.HasValue)
        {
            var excluded = request.ExcludeJobId.Value;
            query = query.Where(x => x.Id != excluded);
        }

        return await query.ToListAsync(cancellationToken);
    }

    private static IReadOnlyList<DateTimeOffset> BuildDaySlots(
        TimeZoneInfo timeZone,
        AvailabilityRules rules,
        DateTime dayLocal,
        DateTimeOffset nowLocal,
        bool enforceFutureBuffer)
    {
        var output = new List<DateTimeOffset>();
        var start = dayLocal.Date.AddHours(rules.WorkdayStartHour);
        var dayEnd = dayLocal.Date.AddHours(rules.WorkdayEndHour);
        var lastStart = dayEnd.AddMinutes(-rules.DefaultDurationMinutes);
        if (lastStart < start)
        {
            return output;
        }

        var minStart = nowLocal.AddMinutes(rules.TodayLeadMinutes);
        for (var local = start; local <= lastStart; local = local.AddMinutes(rules.SlotIntervalMinutes))
        {
            var offset = timeZone.GetUtcOffset(local);
            var slot = new DateTimeOffset(local, offset);
            if (enforceFutureBuffer && slot < minStart)
            {
                continue;
            }

            output.Add(slot);
        }

        return output;
    }

    private static bool HasConflict(
        IReadOnlyList<Job> busyJobs,
        DateTimeOffset start,
        DateTimeOffset end,
        int durationMinutes)
    {
        var safeDuration = Math.Clamp(durationMinutes, 15, 720);
        foreach (var job in busyJobs)
        {
            if (!job.ScheduledAt.HasValue)
            {
                continue;
            }

            var otherStart = job.ScheduledAt.Value;
            var otherEnd = otherStart.AddMinutes(safeDuration);
            if (start < otherEnd && otherStart < end)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class AvailabilityRequest
{
    public string TenantId { get; init; } = "A";
    public long ClientUserId { get; init; }
    public long? ProviderUserId { get; init; }
    public long? ExcludeJobId { get; init; }
    public AvailabilityRules Rules { get; init; } = AvailabilityRules.Default;
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    public DateTimeOffset NowLocal { get; init; } = DateTimeOffset.UtcNow;
    public bool RequireFutureSlotsOnly { get; init; } = true;
}

public sealed class AvailabilityCheckResult
{
    public bool IsAvailable { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
}

public sealed class AvailabilityRules
{
    public static AvailabilityRules Default { get; } = new();

    public int WindowDays { get; init; } = 7;
    public int SlotIntervalMinutes { get; init; } = 60;
    public int WorkdayStartHour { get; init; } = 8;
    public int WorkdayEndHour { get; init; } = 20;
    public int TodayLeadMinutes { get; init; } = 30;
    public int DefaultDurationMinutes { get; init; } = 60;

    public static AvailabilityRules FromConfig(TenantGoogleCalendarConfig? config)
    {
        var startHour = Math.Clamp(config?.AvailabilityWorkdayStartHour ?? 8, 0, 23);
        var endHour = Math.Clamp(config?.AvailabilityWorkdayEndHour ?? 20, 1, 24);
        if (endHour <= startHour)
        {
            endHour = Math.Min(24, startHour + 1);
        }

        return new AvailabilityRules
        {
            WindowDays = Math.Clamp(config?.AvailabilityWindowDays ?? 7, 1, 30),
            SlotIntervalMinutes = Math.Clamp(config?.AvailabilitySlotIntervalMinutes ?? 60, 15, 240),
            WorkdayStartHour = startHour,
            WorkdayEndHour = endHour,
            TodayLeadMinutes = Math.Clamp(config?.AvailabilityTodayLeadMinutes ?? 30, 0, 720),
            DefaultDurationMinutes = Math.Clamp(config?.DefaultDurationMinutes ?? 60, 15, 720)
        };
    }
}
