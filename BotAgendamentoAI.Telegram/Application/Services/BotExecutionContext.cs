using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using BotAgendamentoAI.Telegram.TelegramCompat;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class BotExecutionContext
{
    public string TenantId { get; init; } = "A";
    public BotDbContext Db { get; init; } = null!;
    public ITelegramBotClient Bot { get; init; } = null!;
    public AppUser User { get; init; } = null!;
    public UserSession Session { get; init; } = null!;
    public UserDraft Draft { get; init; } = UserDraft.Empty();
    public TelegramRuntimeSettings Runtime { get; init; } = null!;
}
