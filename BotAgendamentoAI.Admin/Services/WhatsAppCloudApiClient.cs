using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotAgendamentoAI.Admin.Models;

namespace BotAgendamentoAI.Admin.Services;

public sealed class WhatsAppCloudApiClient : IWhatsAppCloudApiClient
{
    private const string GraphApiVersion = "v25.0";

    private readonly HttpClient _httpClient;
    private readonly ILogger<WhatsAppCloudApiClient> _logger;

    public WhatsAppCloudApiClient(HttpClient httpClient, ILogger<WhatsAppCloudApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<WhatsAppSendTextResult> SendTextAsync(
        WhatsAppTenantConfigItem config,
        string recipientPhone,
        string text,
        CancellationToken cancellationToken = default)
    {
        var phoneNumberId = config.PhoneNumberId?.Trim() ?? string.Empty;
        var accessToken = config.AccessToken?.Trim() ?? string.Empty;
        var normalizedRecipient = NormalizeRecipientPhone(recipientPhone);
        var normalizedText = text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(phoneNumberId)
            || string.IsNullOrWhiteSpace(accessToken)
            || string.IsNullOrWhiteSpace(normalizedRecipient)
            || string.IsNullOrWhiteSpace(normalizedText))
        {
            return new WhatsAppSendTextResult
            {
                Success = false,
                Error = "Configuracao ou payload de envio do WhatsApp invalido."
            };
        }

        var payload = new
        {
            messaging_product = "whatsapp",
            to = normalizedRecipient,
            type = "text",
            text = new
            {
                body = normalizedText
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.facebook.com/{GraphApiVersion}/{Uri.EscapeDataString(phoneNumberId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "WhatsApp Cloud API send failed. Tenant: {TenantId}. StatusCode: {StatusCode}. Response: {Response}.",
                config.TenantId,
                (int)response.StatusCode,
                rawResponse);

            return new WhatsAppSendTextResult
            {
                Success = false,
                RawResponse = rawResponse,
                Error = $"HTTP {(int)response.StatusCode}"
            };
        }

        return new WhatsAppSendTextResult
        {
            Success = true,
            MessageId = TryExtractMessageId(rawResponse),
            RawResponse = rawResponse
        };
    }

    private static string NormalizeRecipientPhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return string.Empty;
        }

        var trimmed = phone.Trim();
        if (trimmed.StartsWith("wa:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[3..];
        }

        return new string(trimmed.Where(char.IsDigit).ToArray());
    }

    private static string? TryExtractMessageId(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            if (!document.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var message in messages.EnumerateArray())
            {
                if (!message.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                var messageId = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(messageId))
                {
                    return messageId.Trim();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
