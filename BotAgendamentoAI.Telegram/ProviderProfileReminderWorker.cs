using System.Text.Json;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using BotAgendamentoAI.Telegram.Infrastructure.Services;
using BotAgendamentoAI.Telegram.TelegramCompat;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram;

public sealed class ProviderProfileReminderWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReminderResendCooldown = TimeSpan.FromMinutes(5);

    private readonly TenantConfigService _tenantConfigService;
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly TelegramApiClient _apiClient;
    private readonly TelegramMessageSender _sender;
    private readonly ILogger<ProviderProfileReminderWorker> _logger;

    public ProviderProfileReminderWorker(
        TenantConfigService tenantConfigService,
        IDbContextFactory<BotDbContext> dbFactory,
        TelegramApiClient apiClient,
        TelegramMessageSender sender,
        ILogger<ProviderProfileReminderWorker> logger)
    {
        _tenantConfigService = tenantConfigService;
        _dbFactory = dbFactory;
        _apiClient = apiClient;
        _sender = sender;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker de lembrete de perfil do prestador iniciado. Intervalo={IntervalMinutes}min", SweepInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepTenantsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no sweep do worker de lembrete de perfil.");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }
    }

    private async Task SweepTenantsAsync(CancellationToken cancellationToken)
    {
        var configs = await _tenantConfigService.GetActiveConfigsAsync(cancellationToken);
        if (configs.Count == 0)
        {
            return;
        }

        foreach (var config in configs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(config.BotToken))
            {
                continue;
            }

            try
            {
                await ProcessTenantAsync(config, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha processando lembretes de perfil. tenant={Tenant}", config.TenantId);
            }
        }
    }

    private async Task ProcessTenantAsync(TelegramTenantConfig config, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var providers = await db.Users
            .Include(x => x.ProviderProfile)
            .Include(x => x.Session)
            .Where(x => x.TenantId == config.TenantId
                        && x.IsActive
                        && (x.Role == UserRole.Provider || x.Role == UserRole.Both))
            .OrderBy(x => x.Id)
            .Take(1000)
            .ToListAsync(cancellationToken);

        if (providers.Count == 0)
        {
            return;
        }

        var bot = new HttpTelegramBotClient(_apiClient, config.BotToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var providerUser in providers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var missing = BuildMissingProfileItems(providerUser.ProviderProfile);
            if (missing.Count == 0)
            {
                continue;
            }

            providerUser.Session ??= new UserSession
            {
                UserId = providerUser.Id,
                State = UserContextService.HomeStateForRole(providerUser.Role),
                DraftJson = "{}",
                UpdatedAt = now
            };

            var draft = UserContextService.ParseDraft(providerUser.Session);
            if (draft.ProviderProfileReminderSnoozeUntilUtc.HasValue
                && draft.ProviderProfileReminderSnoozeUntilUtc.Value > now)
            {
                continue;
            }

            if (draft.ProviderProfileReminderLastSentUtc.HasValue
                && now - draft.ProviderProfileReminderLastSentUtc.Value < ReminderResendCooldown)
            {
                continue;
            }

            var reminderText = BuildReminderText(missing);
            await _sender.SendTextAsync(
                db,
                bot,
                config.TenantId,
                providerUser.TelegramUserId,
                providerUser.TelegramUserId,
                reminderText,
                KeyboardFactory.ProviderProfileReminderActions(),
                null,
                cancellationToken);

            draft.ProviderProfileReminderLastSentUtc = now;
            UserContextService.SaveDraft(providerUser.Session, draft);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static List<string> BuildMissingProfileItems(ProviderProfile? profile)
    {
        var missing = new List<string>();
        if (profile is null)
        {
            missing.Add("Categorias de atendimento");
            missing.Add("Raio de atuacao");
            missing.Add("CEP/local base");
            return missing;
        }

        var categories = ParseCategories(profile.CategoriesJson);
        if (categories.Count == 0)
        {
            missing.Add("Categorias de atendimento");
        }

        var validRadiusOptions = new[] { 1, 2, 5, 10, 25, 50 };
        if (!validRadiusOptions.Contains(profile.RadiusKm))
        {
            missing.Add("Raio de atuacao");
        }

        if (!profile.BaseLatitude.HasValue || !profile.BaseLongitude.HasValue)
        {
            missing.Add("CEP/local base");
        }

        return missing;
    }

    private static List<string> ParseCategories(string? categoriesJson)
    {
        if (string.IsNullOrWhiteSpace(categoriesJson))
        {
            return new List<string>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(categoriesJson);
            return parsed?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        catch
        {
            return categoriesJson
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static string BuildReminderText(IReadOnlyCollection<string> missingItems)
    {
        var bulletItems = string.Join("\n", missingItems.Select(x => $"- {x}"));
        return "Seu perfil de prestador esta incompleto:\n"
               + bulletItems
               + "\n\nToque em 'Atualizar agora' para corrigir agora, ou 'Fazer mais tarde' para receber novo lembrete depois.";
    }
}
