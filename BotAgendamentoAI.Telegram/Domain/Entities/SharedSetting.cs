namespace BotAgendamentoAI.Telegram.Domain.Entities;

public sealed class SharedSetting
{
    public string SettingKey { get; set; } = string.Empty;
    public string SettingValue { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
