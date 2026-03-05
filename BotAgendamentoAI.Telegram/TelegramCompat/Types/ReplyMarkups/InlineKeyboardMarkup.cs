namespace BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

public sealed class InlineKeyboardMarkup : IReplyMarkup
{
    public InlineKeyboardMarkup(IEnumerable<IEnumerable<InlineKeyboardButton>> rows)
    {
        InlineKeyboard = rows.Select(row => row.ToArray()).ToArray();
    }

    public InlineKeyboardButton[][] InlineKeyboard { get; }
}
