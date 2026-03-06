using System.Text.Json;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class ProviderReminderSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly ProviderReminderSettings DefaultSettings = new()
    {
        IsEnabled = true,
        SweepIntervalMinutes = 5,
        ReminderResendCooldownMinutes = 5,
        SnoozeHours = 24
    };

    public async Task<ProviderReminderSettings> GetSettingsAsync(
        BotDbContext db,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var safeTenant = NormalizeTenant(tenantId);
        var row = await db.TenantBotConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == safeTenant, cancellationToken);

        if (row is null || string.IsNullOrWhiteSpace(row.MessagesJson))
        {
            return DefaultSettings;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<MessagesConfigStorage>(row.MessagesJson, JsonOptions) ?? new MessagesConfigStorage();
            return new ProviderReminderSettings
            {
                IsEnabled = payload.ProviderReminderEnabled ?? DefaultSettings.IsEnabled,
                SweepIntervalMinutes = ClampSweepInterval(payload.ProviderReminderSweepIntervalMinutes),
                ReminderResendCooldownMinutes = ClampResendCooldown(payload.ProviderReminderResendCooldownMinutes),
                SnoozeHours = ClampSnoozeHours(payload.ProviderReminderSnoozeHours)
            };
        }
        catch
        {
            return DefaultSettings;
        }
    }

    private static int ClampSweepInterval(int? value) => Math.Clamp(value ?? DefaultSettings.SweepIntervalMinutes, 1, 1440);
    private static int ClampResendCooldown(int? value) => Math.Clamp(value ?? DefaultSettings.ReminderResendCooldownMinutes, 1, 1440);
    private static int ClampSnoozeHours(int? value) => Math.Clamp(value ?? DefaultSettings.SnoozeHours, 1, 168);

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

    private sealed class MessagesConfigStorage
    {
        public bool? ProviderReminderEnabled { get; set; }
        public int? ProviderReminderSweepIntervalMinutes { get; set; }
        public int? ProviderReminderResendCooldownMinutes { get; set; }
        public int? ProviderReminderSnoozeHours { get; set; }
    }
}

public sealed class ProviderReminderSettings
{
    public bool IsEnabled { get; set; }
    public int SweepIntervalMinutes { get; set; }
    public int ReminderResendCooldownMinutes { get; set; }
    public int SnoozeHours { get; set; }
}
