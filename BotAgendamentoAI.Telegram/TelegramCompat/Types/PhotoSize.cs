using System.Text.Json.Serialization;

namespace BotAgendamentoAI.Telegram.TelegramCompat.Types;

public sealed class PhotoSize
{
    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}
