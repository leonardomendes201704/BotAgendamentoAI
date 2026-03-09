using BotAgendamentoAI.Admin.Models;

namespace BotAgendamentoAI.Admin.Services;

public interface IWhatsAppCloudApiClient
{
    Task<WhatsAppSendTextResult> SendTextAsync(
        WhatsAppTenantConfigItem config,
        string recipientPhone,
        string text,
        CancellationToken cancellationToken = default);
}

public sealed class WhatsAppSendTextResult
{
    public bool Success { get; set; }
    public string? MessageId { get; set; }
    public string? RawResponse { get; set; }
    public string? Error { get; set; }
}
