namespace BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

public sealed class InlineKeyboardButton
{
    public string Text { get; set; } = string.Empty;
    public string? CallbackData { get; set; }

    public static InlineKeyboardButton WithCallbackData(string text, string callbackData)
    {
        return new InlineKeyboardButton
        {
            Text = text,
            CallbackData = callbackData
        };
    }
}
