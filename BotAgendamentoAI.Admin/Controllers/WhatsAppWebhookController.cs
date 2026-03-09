using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using BotAgendamentoAI.Admin.Realtime;
using BotAgendamentoAI.Admin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

[ApiController]
public sealed class WhatsAppWebhookController : ControllerBase
{
    private readonly IAdminRepository _repository;
    private readonly IWhatsAppCloudApiClient _whatsAppCloudApiClient;
    private readonly IDashboardRealtimeNotifier _realtimeNotifier;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    public WhatsAppWebhookController(
        IAdminRepository repository,
        IWhatsAppCloudApiClient whatsAppCloudApiClient,
        IDashboardRealtimeNotifier realtimeNotifier,
        ILogger<WhatsAppWebhookController> logger)
    {
        _repository = repository;
        _whatsAppCloudApiClient = whatsAppCloudApiClient;
        _realtimeNotifier = realtimeNotifier;
        _logger = logger;
    }

    [HttpGet("/webhooks/whatsapp")]
    public async Task<IActionResult> Verify(CancellationToken cancellationToken)
    {
        var mode = Request.Query["hub.mode"].ToString();
        var token = Request.Query["hub.verify_token"].ToString();
        var challenge = Request.Query["hub.challenge"].ToString();

        if (!string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(challenge))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var configs = await _repository.GetWhatsAppTenantConfigsAsync(activeOnly: false);
        var isValidToken = configs.Any(config =>
            !string.IsNullOrWhiteSpace(config.WebhookVerifyToken)
            && string.Equals(config.WebhookVerifyToken, token, StringComparison.Ordinal));

        return isValidToken
            ? Content(challenge, "text/plain", Encoding.UTF8)
            : StatusCode(StatusCodes.Status403Forbidden);
    }

    [HttpPost("/webhooks/whatsapp")]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        var configs = await _repository.GetWhatsAppTenantConfigsAsync(activeOnly: true);
        if (configs.Count == 0)
        {
            _logger.LogWarning("WhatsApp webhook received on Admin, but there is no active tenant configuration.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var phoneNumberId = TryExtractPhoneNumberId(body);
        var candidateConfigs = FilterCandidateConfigs(configs, phoneNumberId);
        var signatureHeader = Request.Headers["X-Hub-Signature-256"].ToString();

        if (!ValidateSignature(signatureHeader, body, candidateConfigs))
        {
            _logger.LogWarning(
                "WhatsApp webhook signature validation failed. PhoneNumberId: {PhoneNumberId}. CandidateTenants: {TenantIds}.",
                string.IsNullOrWhiteSpace(phoneNumberId) ? "(unknown)" : phoneNumberId,
                string.Join(", ", candidateConfigs.Select(config => config.TenantId)));
            return Unauthorized();
        }

        var inboundMessages = ParseInboundMessages(body);
        var changedTenants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedCount = 0;
        var repliedCount = 0;

        foreach (var inboundMessage in inboundMessages)
        {
            var config = ResolveConfig(candidateConfigs, inboundMessage.PhoneNumberId);
            if (config is null)
            {
                _logger.LogWarning(
                    "WhatsApp inbound message ignored because no tenant configuration matched PhoneNumberId {PhoneNumberId}. MessageId: {MessageId}.",
                    inboundMessage.PhoneNumberId,
                    inboundMessage.MessageId ?? "(none)");
                continue;
            }

            var savedInbound = await _repository.SaveConversationMessageAsync(new ConversationMessageWriteModel
            {
                TenantId = config.TenantId,
                Phone = $"wa:{inboundMessage.From}",
                Direction = "In",
                Role = "user",
                Content = inboundMessage.Content,
                ToolName = "whatsapp_webhook",
                ToolCallId = inboundMessage.MessageId,
                MetadataJson = inboundMessage.RawJson,
                CreatedAtUtc = inboundMessage.CreatedAtUtc
            });

            if (!savedInbound)
            {
                continue;
            }

            processedCount++;
            changedTenants.Add(config.TenantId);

            if (!ShouldReplyToHello(inboundMessage))
            {
                continue;
            }

            var replyText = NormalizeHelloReplyText(config.HelloReplyText);
            var sendResult = await _whatsAppCloudApiClient.SendTextAsync(
                config,
                inboundMessage.From,
                replyText,
                cancellationToken);

            if (!sendResult.Success)
            {
                _logger.LogWarning(
                    "WhatsApp hello reply failed. Tenant: {TenantId}. Recipient: {Recipient}. Error: {Error}.",
                    config.TenantId,
                    inboundMessage.From,
                    sendResult.Error ?? "(unknown)");
                continue;
            }

            var outboundSaved = await _repository.SaveConversationMessageAsync(new ConversationMessageWriteModel
            {
                TenantId = config.TenantId,
                Phone = $"wa:{inboundMessage.From}",
                Direction = "Out",
                Role = "assistant",
                Content = replyText,
                ToolName = "whatsapp_cloud_api",
                ToolCallId = sendResult.MessageId,
                MetadataJson = sendResult.RawResponse,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

            if (outboundSaved)
            {
                repliedCount++;
                changedTenants.Add(config.TenantId);
            }
        }

        foreach (var tenantId in changedTenants)
        {
            await _realtimeNotifier.NotifyTenantChangedAsync(tenantId, cancellationToken);
        }

        _logger.LogInformation(
            "WhatsApp webhook processed. PhoneNumberId: {PhoneNumberId}. InboundMessages: {InboundMessages}. StoredMessages: {StoredMessages}. AutoReplies: {AutoReplies}.",
            string.IsNullOrWhiteSpace(phoneNumberId) ? "(unknown)" : phoneNumberId,
            inboundMessages.Count,
            processedCount,
            repliedCount);

        return Ok();
    }

    private static IReadOnlyList<WhatsAppTenantConfigItem> FilterCandidateConfigs(
        IReadOnlyList<WhatsAppTenantConfigItem> configs,
        string? phoneNumberId)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            return configs;
        }

        var filtered = configs
            .Where(config => string.Equals(config.PhoneNumberId, phoneNumberId, StringComparison.Ordinal))
            .ToList();

        return filtered.Count > 0 ? filtered : configs;
    }

    private static WhatsAppTenantConfigItem? ResolveConfig(
        IReadOnlyList<WhatsAppTenantConfigItem> configs,
        string? phoneNumberId)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            return configs.FirstOrDefault();
        }

        return configs.FirstOrDefault(config =>
                   string.Equals(config.PhoneNumberId, phoneNumberId, StringComparison.Ordinal))
               ?? configs.FirstOrDefault();
    }

    private static bool ValidateSignature(
        string signatureHeader,
        string payload,
        IReadOnlyList<WhatsAppTenantConfigItem> candidateConfigs)
    {
        if (candidateConfigs.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return candidateConfigs.All(config => string.IsNullOrWhiteSpace(config.AppSecret));
        }

        var secrets = candidateConfigs
            .Select(config => config.AppSecret?.Trim() ?? string.Empty)
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (secrets.Count == 0)
        {
            return false;
        }

        return secrets.Any(secret => IsValidSignature(payload, signatureHeader, secret));
    }

    private static bool IsValidSignature(string payload, string signatureHeader, string appSecret)
    {
        var normalizedHeader = signatureHeader.Trim();
        if (!normalizedHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expected = $"sha256={Convert.ToHexString(hashBytes).ToLowerInvariant()}";

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(normalizedHeader.ToLowerInvariant()));
    }

    private static string? TryExtractPhoneNumberId(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value))
                    {
                        continue;
                    }

                    if (!value.TryGetProperty("metadata", out var metadata))
                    {
                        continue;
                    }

                    if (!metadata.TryGetProperty("phone_number_id", out var phoneNumberIdElement))
                    {
                        continue;
                    }

                    var phoneNumberId = phoneNumberIdElement.GetString();
                    if (!string.IsNullOrWhiteSpace(phoneNumberId))
                    {
                        return phoneNumberId.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static List<InboundWhatsAppMessage> ParseInboundMessages(string payload)
    {
        var output = new List<InboundWhatsAppMessage>();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return output;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return output;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value))
                    {
                        continue;
                    }

                    var phoneNumberId = ExtractPhoneNumberId(value);
                    if (!value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var message in messages.EnumerateArray())
                    {
                        var from = message.TryGetProperty("from", out var fromElement)
                            ? NormalizeDigits(fromElement.GetString())
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(from))
                        {
                            continue;
                        }

                        var type = message.TryGetProperty("type", out var typeElement)
                            ? typeElement.GetString()?.Trim() ?? string.Empty
                            : string.Empty;

                        output.Add(new InboundWhatsAppMessage
                        {
                            PhoneNumberId = phoneNumberId,
                            MessageId = message.TryGetProperty("id", out var idElement)
                                ? idElement.GetString()?.Trim()
                                : null,
                            From = from,
                            Type = type,
                            Content = ExtractMessageContent(message, type),
                            RawJson = message.GetRawText(),
                            CreatedAtUtc = ExtractCreatedAtUtc(message)
                        });
                    }
                }
            }
        }
        catch (JsonException)
        {
            return output;
        }

        return output;
    }

    private static string? ExtractPhoneNumberId(JsonElement value)
    {
        if (!value.TryGetProperty("metadata", out var metadata))
        {
            return null;
        }

        if (!metadata.TryGetProperty("phone_number_id", out var phoneNumberIdElement))
        {
            return null;
        }

        return phoneNumberIdElement.GetString()?.Trim();
    }

    private static DateTimeOffset ExtractCreatedAtUtc(JsonElement message)
    {
        if (!message.TryGetProperty("timestamp", out var timestampElement))
        {
            return DateTimeOffset.UtcNow;
        }

        var rawTimestamp = timestampElement.GetString();
        if (long.TryParse(rawTimestamp, out var unixSeconds) && unixSeconds > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return DateTimeOffset.UtcNow;
    }

    private static string ExtractMessageContent(JsonElement message, string type)
    {
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
            && message.TryGetProperty("text", out var textElement)
            && textElement.TryGetProperty("body", out var bodyElement))
        {
            return bodyElement.GetString()?.Trim() ?? string.Empty;
        }

        if (string.Equals(type, "interactive", StringComparison.OrdinalIgnoreCase)
            && message.TryGetProperty("interactive", out var interactiveElement))
        {
            if (interactiveElement.TryGetProperty("button_reply", out var buttonReply)
                && buttonReply.TryGetProperty("title", out var buttonTitle))
            {
                return buttonTitle.GetString()?.Trim() ?? "[Interacao recebida]";
            }

            if (interactiveElement.TryGetProperty("list_reply", out var listReply)
                && listReply.TryGetProperty("title", out var listTitle))
            {
                return listTitle.GetString()?.Trim() ?? "[Interacao recebida]";
            }
        }

        if (string.Equals(type, "location", StringComparison.OrdinalIgnoreCase)
            && message.TryGetProperty("location", out var locationElement))
        {
            var latitude = locationElement.TryGetProperty("latitude", out var latitudeElement)
                ? latitudeElement.GetRawText()
                : string.Empty;
            var longitude = locationElement.TryGetProperty("longitude", out var longitudeElement)
                ? longitudeElement.GetRawText()
                : string.Empty;
            return string.IsNullOrWhiteSpace(latitude) || string.IsNullOrWhiteSpace(longitude)
                ? "[Localizacao recebida]"
                : $"[Localizacao recebida] {latitude}, {longitude}";
        }

        return type.ToLowerInvariant() switch
        {
            "image" => "[Imagem recebida]",
            "document" => "[Documento recebido]",
            "audio" => "[Audio recebido]",
            "video" => "[Video recebido]",
            "sticker" => "[Sticker recebido]",
            _ => string.IsNullOrWhiteSpace(type)
                ? "[Mensagem recebida]"
                : $"[Mensagem recebida: {type}]"
        };
    }

    private static bool ShouldReplyToHello(InboundWhatsAppMessage inboundMessage)
    {
        if (!string.Equals(inboundMessage.Type, "text", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = NormalizeHelloTrigger(inboundMessage.Content);
        return string.Equals(normalized, "oi", StringComparison.Ordinal);
    }

    private static string NormalizeHelloTrigger(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().Trim('.', ',', '!', '?', ';', ':');
        return trimmed.ToLowerInvariant();
    }

    private static string NormalizeHelloReplyText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "Oi! Recebi sua mensagem no WhatsApp."
            : value.Trim();

    private static string NormalizeDigits(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    private sealed class InboundWhatsAppMessage
    {
        public string? PhoneNumberId { get; set; }
        public string? MessageId { get; set; }
        public string From { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string RawJson { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
