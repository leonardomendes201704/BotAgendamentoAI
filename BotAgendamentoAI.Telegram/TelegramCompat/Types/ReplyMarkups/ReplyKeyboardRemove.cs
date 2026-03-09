namespace BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

public sealed class ReplyKeyboardRemove : IReplyMarkup
{
    public bool RemoveKeyboard { get; set; } = true;
}
