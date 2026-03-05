namespace BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

public sealed class KeyboardButton
{
    public string Text { get; set; } = string.Empty;
    public bool RequestLocation { get; set; }

    public KeyboardButton()
    {
    }

    public KeyboardButton(string text)
    {
        Text = text;
    }

    public static KeyboardButton WithRequestLocation(string text)
    {
        return new KeyboardButton(text)
        {
            RequestLocation = true
        };
    }

    public static implicit operator KeyboardButton(string text) => new(text);
}
