using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Application.Callback;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Features.Client;
using BotAgendamentoAI.Telegram.Tests.TestDoubles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;

namespace BotAgendamentoAI.Telegram.Tests;

public sealed class ClientFlowHandlerTests
{
    [Fact]
    public async Task StartWizard_ShouldSetPickCategoryAndResetDraft()
    {
        await using var db = TestContextFactory.CreateDb();
        var user = TestContextFactory.BuildUser(state: BotStates.C_HOME);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var bot = new FakeTelegramBotClient();
        var history = new ConversationHistoryService();
        var sender = new TelegramMessageSender(history);
        var workflow = new JobWorkflowService(sender);
        var photoValidator = new StubPhotoValidator();
        var sut = new ClientFlowHandler(sender, workflow, photoValidator);

        var dirtyDraft = new UserDraft
        {
            Category = "Ar-Condicionado",
            Description = "Problema",
            AddressText = "Rua X 100",
            Cep = "11704150",
            PreferenceCode = "LOW",
            IsUrgent = true
        };
        dirtyDraft.PhotoFileIds.Add("file-1");

        var context = TestContextFactory.BuildExecutionContext(db, bot, user, dirtyDraft);

        await sut.StartWizardAsync(context, new ChatId(user.TelegramUserId), CancellationToken.None);

        Assert.Equal(BotStates.C_PICK_CATEGORY, context.Session.State);
        var savedDraft = UserContextService.ParseDraft(context.Session);
        Assert.Null(savedDraft.Category);
        Assert.Null(savedDraft.Description);
        Assert.Null(savedDraft.AddressText);
        Assert.False(savedDraft.IsUrgent);
        Assert.Empty(savedDraft.PhotoFileIds);
        Assert.NotEmpty(bot.SentTexts);
    }

    [Fact]
    public async Task HandleCallback_ChatExit_ShouldCloseChatAndReturnTracking()
    {
        await using var db = TestContextFactory.CreateDb();
        var user = TestContextFactory.BuildUser(state: BotStates.CHAT_MEDIATED);
        user.Session!.IsChatActive = true;
        user.Session.ChatJobId = 42;
        user.Session.ChatPeerUserId = 999;
        user.Session.ActiveJobId = 42;

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var bot = new FakeTelegramBotClient();
        var history = new ConversationHistoryService();
        var sender = new TelegramMessageSender(history);
        var workflow = new JobWorkflowService(sender);
        var photoValidator = new StubPhotoValidator();
        var sut = new ClientFlowHandler(sender, workflow, photoValidator);
        var context = TestContextFactory.BuildExecutionContext(db, bot, user);

        var parsed = CallbackDataRouter.TryParse("J:42:CHAT:EXIT", out var route);
        Assert.True(parsed);

        var callback = new CallbackQuery
        {
            Id = "cb1",
            Data = "J:42:CHAT:EXIT",
            Message = new Message
            {
                Chat = new Chat { Id = user.TelegramUserId }
            }
        };

        var handled = await sut.HandleCallbackAsync(context, route, callback, CancellationToken.None);

        Assert.True(handled);
        Assert.False(context.Session.IsChatActive);
        Assert.Null(context.Session.ChatPeerUserId);
        Assert.Null(context.Session.ChatJobId);
        Assert.Equal(BotStates.C_TRACKING, context.Session.State);
    }

    [Fact]
    public async Task HandleCallback_ClientHomeMyBookings_ShouldReturnNoBookingsMessage()
    {
        await using var db = TestContextFactory.CreateDb();
        var user = TestContextFactory.BuildUser(state: BotStates.C_HOME);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var bot = new FakeTelegramBotClient();
        var history = new ConversationHistoryService();
        var sender = new TelegramMessageSender(history);
        var workflow = new JobWorkflowService(sender);
        var photoValidator = new StubPhotoValidator();
        var sut = new ClientFlowHandler(sender, workflow, photoValidator);
        var context = TestContextFactory.BuildExecutionContext(db, bot, user);

        var parsed = CallbackDataRouter.TryParse("C:HOME:MY", out var route);
        Assert.True(parsed);

        var callback = new CallbackQuery
        {
            Id = "cb-home-my",
            Data = "C:HOME:MY",
            Message = new Message
            {
                Chat = new Chat { Id = user.TelegramUserId }
            }
        };

        var handled = await sut.HandleCallbackAsync(context, route, callback, CancellationToken.None);

        Assert.True(handled);
        Assert.Contains(bot.SentTexts, x => x.Text.Contains("nao possui agendamentos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleText_UnknownOnHome_ShouldRepeatClientMenu()
    {
        await using var db = TestContextFactory.CreateDb();
        var user = TestContextFactory.BuildUser(state: BotStates.C_HOME);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var bot = new FakeTelegramBotClient();
        var history = new ConversationHistoryService();
        var sender = new TelegramMessageSender(history);
        var workflow = new JobWorkflowService(sender);
        var photoValidator = new StubPhotoValidator();
        var sut = new ClientFlowHandler(sender, workflow, photoValidator);
        var context = TestContextFactory.BuildExecutionContext(db, bot, user);

        var message = new Message
        {
            Chat = new Chat { Id = user.TelegramUserId },
            From = new User { Id = user.TelegramUserId, FirstName = "User" },
            Text = "texto aleatorio"
        };

        await sut.HandleTextAsync(context, message, CancellationToken.None);

        Assert.Contains(bot.SentTexts, x => string.Equals(x.Text, "Menu", StringComparison.Ordinal));
    }
}
