namespace BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

public sealed class ReplyKeyboardMarkup : IReplyMarkup
{
    public ReplyKeyboardMarkup(IEnumerable<IEnumerable<KeyboardButton>> rows)
    {
        Keyboard = rows.Select(row => row.ToArray()).ToArray();
    }

    public KeyboardButton[][] Keyboard { get; }
    public bool ResizeKeyboard { get; set; }
    public bool IsPersistent { get; set; }
    public bool OneTimeKeyboard { get; set; }
}
