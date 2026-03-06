using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Tests;

public sealed class HumanHandoffServiceTests
{
    [Fact]
    public async Task RequestAsync_ShouldCreateOpenSession_AndMoveUserToHumanHandoff()
    {
        await using var db = TestContextFactory.CreateDb();
        var user = TestContextFactory.BuildUser(state: BotStates.C_DESCRIBE_PROBLEM);
        user.Session!.IsChatActive = true;
        user.Session.ChatJobId = 22;
        user.Session.ChatPeerUserId = 77;

        db.Users.Add(user);
        db.TenantBotConfigs.Add(new TenantBotConfig
        {
            TenantId = "A",
            MenuJson = "{}",
            MessagesJson = "{\"humanHandoffText\":\"Um atendente vai assumir esta conversa.\"}",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new HumanHandoffService();
        var result = await sut.RequestAsync(db, "A", user, CancellationToken.None);

        Assert.False(result.IsAlreadyOpen);
        Assert.Equal("Um atendente vai assumir esta conversa.", result.ResponseText);
        Assert.Equal(BotStates.HUMAN_HANDOFF, user.Session.State);
        Assert.False(user.Session.IsChatActive);
        Assert.Null(user.Session.ChatJobId);
        Assert.Null(user.Session.ChatPeerUserId);
        Assert.Single(await db.HumanHandoffSessions.Where(x => x.TenantId == "A" && x.TelegramUserId == user.TelegramUserId && x.IsOpen).ToListAsync());
    }

    [Fact]
    public async Task RequestAsync_WhenOpenSessionExists_ShouldReuseOpenSession()
    {
        await using var db = TestContextFactory.CreateDb();
        var user = TestContextFactory.BuildUser(state: BotStates.C_HOME);
        db.Users.Add(user);
        db.HumanHandoffSessions.Add(new HumanHandoffSession
        {
            TenantId = "A",
            TelegramUserId = user.TelegramUserId,
            AppUserId = user.Id,
            RequestedByRole = "client",
            IsOpen = true,
            RequestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastMessageAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            PreviousState = BotStates.C_HOME
        });
        await db.SaveChangesAsync();

        var sut = new HumanHandoffService();
        var result = await sut.RequestAsync(db, "A", user, CancellationToken.None);

        Assert.True(result.IsAlreadyOpen);
        Assert.Equal(BotStates.HUMAN_HANDOFF, user.Session!.State);
        Assert.Single(await db.HumanHandoffSessions.Where(x => x.TenantId == "A" && x.TelegramUserId == user.TelegramUserId && x.IsOpen).ToListAsync());
    }
}
