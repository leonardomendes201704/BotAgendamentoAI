namespace BotAgendamentoAI.Telegram.TelegramCompat.Types;

public readonly struct ChatId
{
    public ChatId(long identifier)
    {
        Identifier = identifier;
    }

    public long Identifier { get; }

    public static implicit operator ChatId(long value) => new(value);
    public static implicit operator long(ChatId value) => value.Identifier;

    public override string ToString() => Identifier.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
