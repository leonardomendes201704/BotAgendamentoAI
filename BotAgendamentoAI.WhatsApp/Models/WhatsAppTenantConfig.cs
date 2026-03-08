namespace BotAgendamentoAI.WhatsApp.Models;

public sealed class WhatsAppTenantConfig
{
    public string TenantId { get; set; } = "A";
    public bool IsActive { get; set; }
    public string PhoneNumberId { get; set; } = string.Empty;
    public string BusinessAccountId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
    public string WebhookVerifyToken { get; set; } = string.Empty;
}
