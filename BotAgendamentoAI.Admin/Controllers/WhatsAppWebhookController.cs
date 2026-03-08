using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

[ApiController]
public sealed class WhatsAppWebhookController : ControllerBase
{
    private readonly IAdminRepository _repository;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    public WhatsAppWebhookController(IAdminRepository repository, ILogger<WhatsAppWebhookController> logger)
    {
        _repository = repository;
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

        _logger.LogWarning(
            "WhatsApp webhook event received via Admin. PhoneNumberId: {PhoneNumberId}. CandidateTenants: {TenantIds}. PayloadLength: {PayloadLength}.",
            string.IsNullOrWhiteSpace(phoneNumberId) ? "(unknown)" : phoneNumberId,
            string.Join(", ", candidateConfigs.Select(config => config.TenantId)),
            body.Length);

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
}
