using System.Text.Json.Serialization;

namespace BotAgendamentoAI.Telegram.TelegramCompat.Types;

public sealed class Message
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    [JsonPropertyName("date")]
    public long DateUnix { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("chat")]
    public Chat Chat { get; set; } = new();

    [JsonPropertyName("from")]
    public User? From { get; set; }

    [JsonPropertyName("contact")]
    public Contact? Contact { get; set; }

    [JsonPropertyName("photo")]
    public PhotoSize[]? Photo { get; set; }

    [JsonPropertyName("video")]
    public Video? Video { get; set; }

    [JsonPropertyName("location")]
    public Location? Location { get; set; }
}
