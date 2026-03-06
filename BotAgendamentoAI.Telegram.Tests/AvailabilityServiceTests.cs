using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Tests.TestDoubles;

namespace BotAgendamentoAI.Telegram.Tests;

public sealed class AvailabilityServiceTests
{
    [Fact]
    public async Task GetAvailableTimeSlotsAsync_ShouldSkipClientConflicts()
    {
        await using var db = TestContextFactory.CreateDb();
        db.TenantGoogleCalendarConfigs.Add(new TenantGoogleCalendarConfig
        {
            TenantId = "A",
            DefaultDurationMinutes = 60,
            AvailabilityWindowDays = 7,
            AvailabilitySlotIntervalMinutes = 60,
            AvailabilityWorkdayStartHour = 8,
            AvailabilityWorkdayEndHour = 12,
            AvailabilityTodayLeadMinutes = 0
        });

        db.Jobs.Add(new Job
        {
            TenantId = "A",
            ClientUserId = 1,
            ProviderUserId = null,
            Category = "Alvenaria",
            Description = "Conflito cliente",
            Status = JobStatus.WaitingProvider,
            ScheduledAt = new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new AvailabilityService();
        var rules = await sut.GetRulesAsync(db, "A", CancellationToken.None);
        var request = new AvailabilityRequest
        {
            TenantId = "A",
            ClientUserId = 1,
            ProviderUserId = null,
            ExcludeJobId = null,
            Rules = rules,
            TimeZone = TimeZoneInfo.Utc,
            NowLocal = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
            RequireFutureSlotsOnly = false
        };

        var slots = await sut.GetAvailableTimeSlotsAsync(db, request, "20260310", CancellationToken.None);

        Assert.Contains("0800", slots);
        Assert.Contains("0900", slots);
        Assert.DoesNotContain("1000", slots);
        Assert.Contains("1100", slots);
    }

    [Fact]
    public async Task CheckSlotAvailabilityAsync_ShouldDetectProviderConflict()
    {
        await using var db = TestContextFactory.CreateDb();
        db.TenantGoogleCalendarConfigs.Add(new TenantGoogleCalendarConfig
        {
            TenantId = "A",
            DefaultDurationMinutes = 60,
            AvailabilityWindowDays = 7,
            AvailabilitySlotIntervalMinutes = 60,
            AvailabilityWorkdayStartHour = 8,
            AvailabilityWorkdayEndHour = 20,
            AvailabilityTodayLeadMinutes = 0
        });

        db.Jobs.Add(new Job
        {
            TenantId = "A",
            ClientUserId = 11,
            ProviderUserId = 99,
            Category = "Eletrica",
            Description = "Conflito prestador",
            Status = JobStatus.Accepted,
            ScheduledAt = new DateTimeOffset(2026, 3, 11, 15, 0, 0, TimeSpan.Zero),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new AvailabilityService();
        var rules = await sut.GetRulesAsync(db, "A", CancellationToken.None);
        var request = new AvailabilityRequest
        {
            TenantId = "A",
            ClientUserId = 22,
            ProviderUserId = 99,
            ExcludeJobId = null,
            Rules = rules,
            TimeZone = TimeZoneInfo.Utc,
            NowLocal = new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero),
            RequireFutureSlotsOnly = false
        };

        var check = await sut.CheckSlotAvailabilityAsync(
            db,
            request,
            new DateTimeOffset(2026, 3, 11, 15, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.False(check.IsAvailable);
    }
}
