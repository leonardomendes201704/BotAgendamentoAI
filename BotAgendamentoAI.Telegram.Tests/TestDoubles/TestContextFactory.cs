using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Tests.TestDoubles;

internal static class TestContextFactory
{
    public static BotDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new BotDbContext(options);
    }

    public static TelegramRuntimeSettings CreateRuntime()
    {
        return new TelegramRuntimeSettings
        {
            DatabasePath = "memory",
            TimeZone = TimeZoneInfo.Utc,
            TenantIdleDelaySeconds = 1,
            SessionExpiryMinutes = 180,
            HistoryLimitPerContext = 20,
            EnablePhotoValidation = false
        };
    }

    public static AppUser BuildUser(
        long userId = 1,
        long telegramUserId = 5511999999999,
        UserRole role = UserRole.Client,
        string state = BotStates.C_HOME)
    {
        var user = new AppUser
        {
            Id = userId,
            TenantId = "A",
            TelegramUserId = telegramUserId,
            Name = "Teste",
            Username = "teste",
            Role = role,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        user.Session = new UserSession
        {
            UserId = userId,
            State = state,
            DraftJson = "{}",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return user;
    }

    public static BotExecutionContext BuildExecutionContext(
        BotDbContext db,
        FakeTelegramBotClient bot,
        AppUser user,
        UserDraft? draft = null)
    {
        var activeDraft = draft ?? UserDraft.Empty();
        UserContextService.SaveDraft(user.Session!, activeDraft);

        return new BotExecutionContext
        {
            TenantId = "A",
            Db = db,
            Bot = bot,
            User = user,
            Session = user.Session!,
            Draft = activeDraft,
            Runtime = CreateRuntime()
        };
    }
}
