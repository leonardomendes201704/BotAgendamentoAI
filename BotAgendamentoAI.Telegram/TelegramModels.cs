using System.Text.Json.Serialization;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.Enums;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.InputFiles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram;

public sealed class TelegramApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; set; }
}

public sealed class TelegramGetUpdatesRequest
{
    [JsonPropertyName("offset")]
    public long Offset { get; set; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; }

    [JsonPropertyName("allowed_updates")]
    public IReadOnlyList<string> AllowedUpdates { get; set; } = new[] { "message", "callback_query" };
}

public sealed class TelegramSendMessageRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("parse_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParseMode { get; set; }

    [JsonPropertyName("reply_markup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ReplyMarkup { get; set; }
}

public sealed class TelegramSendPhotoRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("photo")]
    public string Photo { get; set; } = string.Empty;

    [JsonPropertyName("caption")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; set; }

    [JsonPropertyName("parse_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParseMode { get; set; }

    [JsonPropertyName("reply_markup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ReplyMarkup { get; set; }
}

public sealed class TelegramSendMediaGroupRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("media")]
    public IReadOnlyList<TelegramMediaItem> Media { get; set; } = Array.Empty<TelegramMediaItem>();
}

public sealed class TelegramMediaItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "photo";

    [JsonPropertyName("media")]
    public string Media { get; set; } = string.Empty;

    [JsonPropertyName("caption")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; set; }

    [JsonPropertyName("parse_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParseMode { get; set; }
}

public sealed class TelegramSendLocationRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}

public sealed class TelegramAnswerCallbackRequest
{
    [JsonPropertyName("callback_query_id")]
    public string CallbackQueryId { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("show_alert")]
    public bool ShowAlert { get; set; }
}

public static class TelegramReplyMarkupSerializer
{
    public static object? Convert(IReplyMarkup? markup)
    {
        return markup switch
        {
            null => null,
            InlineKeyboardMarkup inline => new
            {
                inline_keyboard = inline.InlineKeyboard
                    .Select(row => row.Select(button => new
                    {
                        text = button.Text,
                        callback_data = button.CallbackData
                    }).ToArray())
                    .ToArray()
            },
            ReplyKeyboardMarkup reply => new
            {
                keyboard = reply.Keyboard
                    .Select(row => row.Select(button =>
                        button.RequestLocation
                            ? new Dictionary<string, object?>
                            {
                                ["text"] = button.Text,
                                ["request_location"] = true
                            }
                            : new Dictionary<string, object?> { ["text"] = button.Text })
                    .ToArray())
                    .ToArray(),
                resize_keyboard = reply.ResizeKeyboard,
                is_persistent = reply.IsPersistent,
                one_time_keyboard = reply.OneTimeKeyboard
            },
            ReplyKeyboardRemove remove => new
            {
                remove_keyboard = remove.RemoveKeyboard
            },
            _ => null
        };
    }

    public static string? Convert(ParseMode mode)
    {
        return mode switch
        {
            ParseMode.Html => "HTML",
            ParseMode.Markdown => "Markdown",
            _ => null
        };
    }

    public static IReadOnlyList<TelegramMediaItem> Convert(IEnumerable<IAlbumInputMedia> media)
    {
        return media.Select(item => new TelegramMediaItem
        {
            Type = item.Type,
            Media = item.Media,
            Caption = item.Caption,
            ParseMode = item.ParseMode.HasValue ? Convert(item.ParseMode.Value) : null
        }).ToList();
    }
}
