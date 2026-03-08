using System.Text.Json;
using BotAgendamentoAI.WhatsApp.Data;
using BotAgendamentoAI.WhatsApp.Models;
using BotAgendamentoAI.WhatsApp.Options;
using BotAgendamentoAI.WhatsApp.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<WhatsAppRuntimeOptions>(builder.Configuration.GetSection("WhatsAppRuntime"));
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var defaultConnection = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(defaultConnection))
{
    builder.Services.AddSingleton<IWhatsAppTenantConfigRepository, SqlServerWhatsAppTenantConfigRepository>();
}
else
{
    builder.Services.AddSingleton<IWhatsAppTenantConfigRepository, SqliteWhatsAppTenantConfigRepository>();
}

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<IWhatsAppTenantConfigRepository>();
    await repository.InitializeAsync();
}

app.MapGet("/", () => Results.Ok(new
{
    service = "whatsapp-webhook",
    status = "ok",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/webhooks/whatsapp", async (HttpRequest request, IWhatsAppTenantConfigRepository repository, CancellationToken cancellationToken) =>
{
    var mode = request.Query["hub.mode"].ToString();
    var token = request.Query["hub.verify_token"].ToString();
    var challenge = request.Query["hub.challenge"].ToString();

    if (!string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(token)
        || string.IsNullOrWhiteSpace(challenge))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var configs = await repository.GetConfigsAsync(activeOnly: false, cancellationToken);
    var isValidToken = configs.Any(config =>
        !string.IsNullOrWhiteSpace(config.WebhookVerifyToken)
        && string.Equals(config.WebhookVerifyToken, token, StringComparison.Ordinal));

    return isValidToken
        ? Results.Text(challenge, "text/plain")
        : Results.StatusCode(StatusCodes.Status403Forbidden);
});

app.MapPost("/webhooks/whatsapp", async (HttpContext httpContext, IWhatsAppTenantConfigRepository repository, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("WhatsAppWebhook");

    using var reader = new StreamReader(httpContext.Request.Body);
    var body = await reader.ReadToEndAsync(cancellationToken);

    var configs = await repository.GetConfigsAsync(activeOnly: true, cancellationToken);
    if (configs.Count == 0)
    {
        logger.LogWarning("WhatsApp webhook received, but there is no active tenant configuration.");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var phoneNumberId = TryExtractPhoneNumberId(body);
    var candidateConfigs = FilterCandidateConfigs(configs, phoneNumberId);
    var signatureHeader = httpContext.Request.Headers["X-Hub-Signature-256"].ToString();

    if (!ValidateSignature(signatureHeader, body, candidateConfigs, logger))
    {
        return Results.Unauthorized();
    }

    logger.LogWarning(
        "WhatsApp webhook event received. PhoneNumberId: {PhoneNumberId}. CandidateTenants: {TenantIds}. PayloadLength: {PayloadLength}.",
        string.IsNullOrWhiteSpace(phoneNumberId) ? "(unknown)" : phoneNumberId,
        string.Join(", ", candidateConfigs.Select(config => config.TenantId)),
        body.Length);

    return Results.Ok();
});

app.Run();

static IReadOnlyList<WhatsAppTenantConfig> FilterCandidateConfigs(IReadOnlyList<WhatsAppTenantConfig> configs, string? phoneNumberId)
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

static bool ValidateSignature(
    string signatureHeader,
    string payload,
    IReadOnlyList<WhatsAppTenantConfig> candidateConfigs,
    ILogger logger)
{
    if (candidateConfigs.Count == 0)
    {
        logger.LogWarning("WhatsApp webhook received, but no tenant configuration matched the payload.");
        return false;
    }

    if (string.IsNullOrWhiteSpace(signatureHeader))
    {
        logger.LogWarning("WhatsApp webhook received without X-Hub-Signature-256 header.");
        return candidateConfigs.All(config => string.IsNullOrWhiteSpace(config.AppSecret));
    }

    var secrets = candidateConfigs
        .Select(config => config.AppSecret?.Trim() ?? string.Empty)
        .Where(secret => !string.IsNullOrWhiteSpace(secret))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    if (secrets.Count == 0)
    {
        logger.LogWarning("WhatsApp webhook signature header received, but there is no configured App Secret to validate it.");
        return false;
    }

    return secrets.Any(secret => WhatsAppSignatureValidator.IsValid(payload, signatureHeader, secret));
}

static string? TryExtractPhoneNumberId(string payload)
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
