using System.Net.Http.Json;
using System.Text.Json;

namespace BotAgendamentoAI.Telegram;

public sealed class TelegramApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramApiClient> _logger;

    public TelegramApiClient(HttpClient httpClient, ILogger<TelegramApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(75);
    }

    public async Task<TelegramApiResponse<List<TelegramUpdate>>> GetUpdatesAsync(
        string botToken,
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var payload = new TelegramGetUpdatesRequest
        {
            Offset = offset,
            Timeout = Math.Clamp(timeoutSeconds, 5, 50),
            AllowedUpdates = new[] { "message" }
        };

        return await PostAsync<List<TelegramUpdate>>(botToken, "getUpdates", payload, cancellationToken);
    }

    public async Task<TelegramApiResponse<TelegramMessage>> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        CancellationToken cancellationToken)
    {
        var payload = new TelegramSendMessageRequest
        {
            ChatId = chatId,
            Text = text ?? string.Empty
        };

        return await PostAsync<TelegramMessage>(botToken, "sendMessage", payload, cancellationToken);
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
