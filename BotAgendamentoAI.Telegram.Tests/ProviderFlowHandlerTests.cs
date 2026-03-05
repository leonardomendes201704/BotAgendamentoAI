using BotAgendamentoAI.Telegram.Application.Callback;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Features.Provider;
using BotAgendamentoAI.Telegram.Tests.TestDoubles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Tests;

public sealed class ProviderFlowHandlerTests
{
    [Fact]
    public async Task HandleCallback_TimelineOnTheWay_ShouldUpdateStatusAndNotifyClient()
    {
        await using var db = TestContextFactory.CreateDb();

        var client = TestContextFactory.BuildUser(userId: 1, telegramUserId: 551100000001, role: UserRole.Client, state: BotStates.C_HOME);
        var provider = TestContextFactory.BuildUser(userId: 2, telegramUserId: 551100000002, role: UserRole.Provider, state: BotStates.P_HOME);
        db.Users.AddRange(client, provider);
        db.ProvidersProfile.Add(new ProviderProfile
        {
            UserId = provider.Id,
            Bio = "Tecnico",
            CategoriesJson = "[]",
            RadiusKm = 10,
            IsAvailable = true
        });

        var job = new Job
        {
            TenantId = "A",
            ClientUserId = client.Id,
            ProviderUserId = provider.Id,
            Category = "Ar-Condicionado",
            Description = "Erro CH26",
            Status = JobStatus.Accepted,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var bot = new FakeTelegramBotClient();
        var history = new ConversationHistoryService();
        var sender = new TelegramMessageSender(history);
        var workflow = new JobWorkflowService(sender);
        var sut = new ProviderFlowHandler(sender, workflow);
        var context = TestContextFactory.BuildExecutionContext(db, bot, provider);

        var payload = $"J:{job.Id}:S:OTW";
        var parsed = CallbackDataRouter.TryParse(payload, out var route);
        Assert.True(parsed);

        var callback = new CallbackQuery
        {
            Id = "cb-otw",
            Data = payload,
            Message = new Message { Chat = new Chat { Id = provider.TelegramUserId } }
        };

        var handled = await sut.HandleCallbackAsync(context, route, callback, CancellationToken.None);

        Assert.True(handled);

        var reloaded = await db.Jobs.AsNoTracking().FirstAsync(x => x.Id == job.Id);
        Assert.Equal(JobStatus.OnTheWay, reloaded.Status);
        Assert.Contains(bot.SentTexts, x => x.ChatId == provider.TelegramUserId);
        Assert.Contains(bot.SentTexts, x => x.ChatId == client.TelegramUserId);
    }

    [Fact]
    public async Task ProviderProfileEdit_Radius_ShouldPersistAndReturnHome()
    {
        await using var db = TestContextFactory.CreateDb();

        var provider = TestContextFactory.BuildUser(userId: 2, telegramUserId: 551100000002, role: UserRole.Provider, state: BotStates.P_HOME);
        db.Users.Add(provider);
        db.ProvidersProfile.Add(new ProviderProfile
        {
            UserId = provider.Id,
            Bio = "Bio atual",
            CategoriesJson = "[]",
            RadiusKm = 10,
            IsAvailable = true
        });
        await db.SaveChangesAsync();

        var bot = new FakeTelegramBotClient();
        var history = new ConversationHistoryService();
        var sender = new TelegramMessageSender(history);
        var workflow = new JobWorkflowService(sender);
        var sut = new ProviderFlowHandler(sender, workflow);
        var context = TestContextFactory.BuildExecutionContext(db, bot, provider);

        var enterPayload = "P:PRF:RAD";
        var parsed = CallbackDataRouter.TryParse(enterPayload, out var enterRoute);
        Assert.True(parsed);

        var callback = new CallbackQuery
        {
            Id = "cb-rad",
            Data = enterPayload,
            Message = new Message { Chat = new Chat { Id = provider.TelegramUserId } }
        };

        var entered = await sut.HandleCallbackAsync(context, enterRoute, callback, CancellationToken.None);
        Assert.True(entered);
        Assert.Equal(BotStates.P_PROFILE_EDIT, context.Session.State);
        Assert.Equal("P:RAD", context.Draft.PreferenceCode);

        await sut.HandleTextAsync(context, new Message
        {
            Chat = new Chat { Id = provider.TelegramUserId },
            Text = "25"
        }, CancellationToken.None);

        var profile = await db.ProvidersProfile.AsNoTracking().FirstAsync(x => x.UserId == provider.Id);
        Assert.Equal(25, profile.RadiusKm);
        Assert.Equal(BotStates.P_HOME, context.Session.State);
    }
}
