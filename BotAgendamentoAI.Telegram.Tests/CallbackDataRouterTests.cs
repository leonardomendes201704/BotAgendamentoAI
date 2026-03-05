using BotAgendamentoAI.Telegram.Application.Callback;

namespace BotAgendamentoAI.Telegram.Tests;

public sealed class CallbackDataRouterTests
{
    [Fact]
    public void TryParse_ShouldParseValidCallback()
    {
        var ok = CallbackDataRouter.TryParse("C:CAT:123", out var route);

        Assert.True(ok);
        Assert.Equal("C", route.Scope);
        Assert.Equal("CAT", route.Action);
        Assert.Equal("123", route.Arg1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_ShouldRejectInvalidPayload(string? payload)
    {
        var ok = CallbackDataRouter.TryParse(payload, out _);

        Assert.False(ok);
    }
}
