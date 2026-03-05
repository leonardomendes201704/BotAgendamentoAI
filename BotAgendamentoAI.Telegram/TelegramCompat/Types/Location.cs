using System.Text.Json.Serialization;

namespace BotAgendamentoAI.Telegram.TelegramCompat.Types;

public sealed class Location
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}
