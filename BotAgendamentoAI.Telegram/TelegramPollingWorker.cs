using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Infrastructure.Services;
using BotAgendamentoAI.Telegram.TelegramCompat;

namespace BotAgendamentoAI.Telegram;

public sealed class TelegramPollingWorker : BackgroundService
{
    private readonly TenantConfigService _tenantConfigService;
    private readonly MarketplaceBotOrchestrator _orchestrator;
    private readonly TelegramApiClient _apiClient;
    private readonly TelegramRuntimeSettings _runtime;
    private readonly ILogger<TelegramPollingWorker> _logger;

    public TelegramPollingWorker(
        TenantConfigService tenantConfigService,
        MarketplaceBotOrchestrator orchestrator,
        TelegramApiClient apiClient,
        TelegramRuntimeSettings runtime,
        ILogger<TelegramPollingWorker> logger)
    {
        _tenantConfigService = tenantConfigService;
        _orchestrator = orchestrator;
        _apiClient = apiClient;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Telegram HTTP worker iniciado. DB={DbPath} TZ={Timezone}",
            _runtime.DatabasePath,
            _runtime.TimeZone.Id);

        var lastNoConfigLogUtc = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<Domain.Entities.TelegramTenantConfig> activeConfigs;
            try
            {
                activeConfigs = await _tenantConfigService.GetActiveConfigsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar configuracoes Telegram ativas.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _runtime.TenantIdleDelaySeconds)), stoppingToken);
                continue;
            }

            if (activeConfigs.Count == 0)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastNoConfigLogUtc > TimeSpan.FromMinutes(2))
                {
                    _logger.LogWarning("Nenhum tenant Telegram ativo com token configurado no banco.");
                    lastNoConfigLogUtc = now;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _runtime.TenantIdleDelaySeconds)), stoppingToken);
                continue;
            }

            foreach (var config in activeConfigs)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await PollTenantAsync(config, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro no polling tenant={Tenant}", config.TenantId);
                }
            }
        }
    }

    private async Task PollTenantAsync(Domain.Entities.TelegramTenantConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            return;
        }

        var timeoutSeconds = Math.Clamp(config.PollingTimeoutSeconds, 5, 50);
        var offset = config.LastUpdateId > 0 ? config.LastUpdateId + 1 : 0;

        var response = await _apiClient.GetUpdatesAsync(config.BotToken, offset, timeoutSeconds, cancellationToken);
        if (!response.Ok)
        {
            _logger.LogWarning(
                "Polling Telegram falhou tenant={Tenant}. code={Code} desc={Desc}",
                config.TenantId,
                response.ErrorCode,
                response.Description);
            return;
        }

        var updates = response.Result ?? new List<global::BotAgendamentoAI.Telegram.TelegramCompat.Types.Update>();
        if (updates.Count == 0)
        {
            return;
        }

        var client = new HttpTelegramBotClient(_apiClient, config.BotToken);
        long maxUpdateId = config.LastUpdateId;

        foreach (var update in updates.OrderBy(x => x.UpdateId))
        {
            maxUpdateId = Math.Max(maxUpdateId, update.UpdateId);
            try
            {
                await _orchestrator.HandleUpdateAsync(config.TenantId, client, update, _runtime, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Erro processando update tenant={Tenant} updateId={UpdateId}. Update sera ignorado para evitar loop.",
                    config.TenantId,
                    update.UpdateId);
            }
        }

        if (maxUpdateId > config.LastUpdateId)
        {
            await _tenantConfigService.UpdateLastUpdateIdAsync(config.TenantId, maxUpdateId, cancellationToken);
        }
    }
}
