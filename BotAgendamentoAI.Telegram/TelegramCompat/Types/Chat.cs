using System.Text.Json.Serialization;

namespace BotAgendamentoAI.Telegram.TelegramCompat.Types;

public sealed class Chat
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
