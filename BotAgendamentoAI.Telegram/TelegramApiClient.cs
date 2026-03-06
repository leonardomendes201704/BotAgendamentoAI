using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.Enums;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.InputFiles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram;

public sealed class TelegramApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramApiClient> _logger;

    public TelegramApiClient(HttpClient httpClient, ILogger<TelegramApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(90);
    }

    public Task<TelegramApiResponse<List<Update>>> GetUpdatesAsync(
        string botToken,
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var payload = new TelegramGetUpdatesRequest
        {
            Offset = offset,
            Timeout = Math.Clamp(timeoutSeconds, 5, 50),
            AllowedUpdates = new[] { "message", "callback_query" }
        };

        return PostAsync<List<Update>>(botToken, "getUpdates", payload, cancellationToken);
    }

    public Task<TelegramApiResponse<Message>> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        ParseMode parseMode,
        IReplyMarkup? replyMarkup,
        CancellationToken cancellationToken)
    {
        var payload = new TelegramSendMessageRequest
        {
            ChatId = chatId,
            Text = text ?? string.Empty,
            ParseMode = TelegramReplyMarkupSerializer.Convert(parseMode),
            ReplyMarkup = TelegramReplyMarkupSerializer.Convert(replyMarkup)
        };

        return PostAsync<Message>(botToken, "sendMessage", payload, cancellationToken);
    }

    public Task<TelegramApiResponse<Message>> SendPhotoAsync(
        string botToken,
        long chatId,
        string fileId,
        string? caption,
        ParseMode parseMode,
        InlineKeyboardMarkup? replyMarkup,
        CancellationToken cancellationToken)
    {
        var payload = new TelegramSendPhotoRequest
        {
            ChatId = chatId,
            Photo = fileId,
            Caption = caption,
            ParseMode = TelegramReplyMarkupSerializer.Convert(parseMode),
            ReplyMarkup = TelegramReplyMarkupSerializer.Convert(replyMarkup)
        };

        return PostAsync<Message>(botToken, "sendPhoto", payload, cancellationToken);
    }

    public Task<TelegramApiResponse<List<Message>>> SendMediaGroupAsync(
        string botToken,
        long chatId,
        IEnumerable<IAlbumInputMedia> media,
        CancellationToken cancellationToken)
    {
        var payload = new TelegramSendMediaGroupRequest
        {
            ChatId = chatId,
            Media = TelegramReplyMarkupSerializer.Convert(media)
        };

        return PostAsync<List<Message>>(botToken, "sendMediaGroup", payload, cancellationToken);
    }

    public Task<TelegramApiResponse<Message>> SendLocationAsync(
        string botToken,
        long chatId,
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var payload = new TelegramSendLocationRequest
        {
            ChatId = chatId,
            Latitude = latitude,
            Longitude = longitude
        };

        return PostAsync<Message>(botToken, "sendLocation", payload, cancellationToken);
    }

    public Task<TelegramApiResponse<bool>> AnswerCallbackQueryAsync(
        string botToken,
        string callbackQueryId,
        string? text,
        bool showAlert,
        CancellationToken cancellationToken)
    {
        var payload = new TelegramAnswerCallbackRequest
        {
            CallbackQueryId = callbackQueryId,
            Text = text,
            ShowAlert = showAlert
        };

        return PostAsync<bool>(botToken, "answerCallbackQuery", payload, cancellationToken);
    }

    private async Task<TelegramApiResponse<T>> PostAsync<T>(
        string botToken,
        string methodName,
        object payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new TelegramApiResponse<T>
            {
                Ok = false,
                Description = "Telegram bot token vazio."
            };
        }

        var endpoint = $"https://api.telegram.org/bot{botToken.Trim()}/{methodName}";

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, JsonOptions, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            var parsed = TryDeserialize<T>(responseText);
            if (parsed is not null)
            {
                if (!parsed.Ok)
                {
                    _logger.LogWarning(
                        "Telegram API retornou erro em {Method}. code={Code} desc={Desc}",
                        methodName,
                        parsed.ErrorCode,
                        parsed.Description);
                }

                return parsed;
            }

            if (!response.IsSuccessStatusCode)
            {
                return new TelegramApiResponse<T>
                {
                    Ok = false,
                    Description = $"HTTP {(int)response.StatusCode} ao chamar Telegram {methodName}."
                };
            }

            return new TelegramApiResponse<T>
            {
                Ok = false,
                Description = "Resposta Telegram invalida."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha HTTP Telegram em {Method}", methodName);
            return new TelegramApiResponse<T>
            {
                Ok = false,
                Description = $"Erro ao chamar Telegram: {ex.Message}"
            };
        }
    }

    private static TelegramApiResponse<T>? TryDeserialize<T>(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TelegramApiResponse<T>>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
