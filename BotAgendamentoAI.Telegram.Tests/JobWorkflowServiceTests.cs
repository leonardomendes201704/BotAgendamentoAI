using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Tests.TestDoubles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Tests;

public sealed class JobWorkflowServiceTests
{
    [Fact]
    public async Task ConfirmDraft_ShouldCreateWaitingProviderJobWithPhotos()
    {
        await using var db = TestContextFactory.CreateDb();
        var user = TestContextFactory.BuildUser(state: BotStates.C_CONFIRM);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var bot = new FakeTelegramBotClient();
        var history = new ConversationHistoryService();
        var sender = new TelegramMessageSender(history);
        var sut = new JobWorkflowService(sender);

        var draft = new UserDraft
        {
            Category = "Ar-Condicionado",
            Description = "Erro CH26 no split",
            AddressText = "Rua Monteiro Lobato, 136, Praia Grande - SP, CEP 11704-150",
            Cep = "11704150",
            IsUrgent = false,
            ScheduledAt = new DateTimeOffset(2026, 3, 6, 10, 0, 0, TimeSpan.Zero),
            PreferenceCode = "LOW"
        };
        draft.PhotoFileIds.Add("file-before-1");
        draft.PhotoFileIds.Add("file-before-2");

        var context = TestContextFactory.BuildExecutionContext(db, bot, user, draft);

        var created = await sut.ConfirmDraftAsync(context, new ChatId(user.TelegramUserId), CancellationToken.None);

        Assert.True(created.Id > 0);
        Assert.Equal(JobStatus.WaitingProvider, created.Status);
        Assert.Equal("Ar-Condicionado", created.Category);

        var savedJob = await db.Jobs.AsNoTracking().FirstAsync(x => x.Id == created.Id);
        Assert.Equal(JobStatus.WaitingProvider, savedJob.Status);

        var photos = await db.JobPhotos.AsNoTracking().Where(x => x.JobId == created.Id).ToListAsync();
        Assert.Equal(2, photos.Count);
        Assert.All(photos, p => Assert.Equal("before", p.Kind));

        Assert.NotEmpty(bot.SentTexts);
    }
}
