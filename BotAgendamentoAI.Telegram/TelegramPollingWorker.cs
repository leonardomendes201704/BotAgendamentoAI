using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using BotAgendamentoAI.Bot;
using BotAgendamentoAI.Data;
using BotAgendamentoAI.Domain;

namespace BotAgendamentoAI.Telegram;

public sealed class TelegramPollingWorker : BackgroundService
{
    private readonly ConversationRepository _repository;
    private readonly SecretaryBot _bot;
    private readonly TelegramApiClient _telegramApiClient;
    private readonly TelegramRuntimeSettings _runtime;
    private readonly ILogger<TelegramPollingWorker> _logger;
    private readonly ConcurrentDictionary<string, TenantSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public TelegramPollingWorker(
        ConversationRepository repository,
        SecretaryBot bot,
        TelegramApiClient telegramApiClient,
        TelegramRuntimeSettings runtime,
        ILogger<TelegramPollingWorker> logger)
    {
        _repository = repository;
        _bot = bot;
        _telegramApiClient = telegramApiClient;
        _runtime = runtime;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync();
        _logger.LogInformation("Telegram worker iniciado. DB={DbPath} | Timezone={Timezone} | Model={Model}",
            _runtime.DatabasePath,
            _runtime.TimeZone.Id,
            _runtime.OpenAiModel);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastNoConfigLogUtc = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<TelegramBotConfig> activeConfigs;
            try
            {
                activeConfigs = await _repository.GetActiveTelegramConfigs();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar configuracoes Telegram ativas.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _runtime.TenantIdleDelaySeconds)), stoppingToken);
                continue;
            }

            await SyncSessionsAsync(activeConfigs);

            if (activeConfigs.Count == 0)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastNoConfigLogUtc > TimeSpan.FromMinutes(2))
                {
                    _logger.LogWarning("Nenhum tenant Telegram ativo com token configurado. Verifique Settings no Admin.");
                    lastNoConfigLogUtc = now;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _runtime.TenantIdleDelaySeconds)), stoppingToken);
                continue;
            }

            var pollTasks = activeConfigs
                .Select(config => PollTenantAsync(config, stoppingToken))
                .ToArray();

            try
            {
                await Task.WhenAll(pollTasks);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada no ciclo de polling Telegram.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task SyncSessionsAsync(IReadOnlyList<TelegramBotConfig> activeConfigs)
    {
        var activeTenants = new HashSet<string>(
            activeConfigs.Select(config => NormalizeTenant(config.TenantId)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var config in activeConfigs)
        {
            var tenant = NormalizeTenant(config.TenantId);
            _sessions.AddOrUpdate(
                tenant,
                _ => new TenantSession(
                    config,
                    _repository,
                    _bot,
                    _telegramApiClient,
                    _logger),
                (_, existing) =>
                {
                    existing.UpdateConfig(config);
                    return existing;
                });
        }

        var toRemove = _sessions.Keys
            .Where(tenant => !activeTenants.Contains(tenant))
            .ToArray();

        foreach (var tenant in toRemove)
        {
            if (_sessions.TryRemove(tenant, out var session))
            {
                await session.DisposeAsync();
            }
        }
    }

    private async Task PollTenantAsync(TelegramBotConfig config, CancellationToken cancellationToken)
    {
        var tenant = NormalizeTenant(config.TenantId);
        if (!_sessions.TryGetValue(tenant, out var session))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            return;
        }

        var offset = config.LastUpdateId > 0 ? config.LastUpdateId + 1 : 0;
        var timeoutSeconds = Math.Clamp(config.PollingTimeoutSeconds, 5, 50);
        var response = await _telegramApiClient.GetUpdatesAsync(config.BotToken, offset, timeoutSeconds, cancellationToken);

        if (!response.Ok)
        {
            _logger.LogWarning("Polling Telegram falhou tenant={Tenant}. code={Code} desc={Desc}",
                tenant,
                response.ErrorCode,
                response.Description);
            return;
        }

        var updates = response.Result ?? new List<TelegramUpdate>();
        if (updates.Count == 0)
        {
            return;
        }

        long maxUpdateId = config.LastUpdateId;

        foreach (var update in updates.OrderBy(u => u.UpdateId))
        {
            maxUpdateId = Math.Max(maxUpdateId, update.UpdateId);

            if (!TryExtractIncomingMessage(update, out var chatId, out var inboundText))
            {
                continue;
            }

            await session.EnqueueAsync(chatId, inboundText);
        }

        if (maxUpdateId > config.LastUpdateId)
        {
            await _repository.UpdateTelegramLastUpdateId(tenant, maxUpdateId);
        }
    }

    private static bool TryExtractIncomingMessage(
        TelegramUpdate update,
        out long chatId,
        out string inboundText)
    {
        chatId = 0;
        inboundText = string.Empty;

        var message = update.Message;
        if (message?.Chat is null)
        {
            return false;
        }

        chatId = message.Chat.Id;
        if (chatId == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            inboundText = message.Text.Trim();
            return inboundText.Length > 0;
        }

        var contactDigits = Regex.Replace(message.Contact?.PhoneNumber ?? string.Empty, @"\D", string.Empty);
        if (contactDigits.Length is >= 10 and <= 13)
        {
            inboundText = $"meu telefone e {contactDigits}";
            return true;
        }

        return false;
    }

    private static string NormalizeTenant(string tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

    private sealed class TenantSession : IAsyncDisposable
    {
        private readonly TelegramApiClient _telegramApiClient;
        private readonly ILogger _logger;
        private readonly MessagePoolingDispatcher _dispatcher;
        private readonly object _sync = new();

        private TelegramBotConfig _config;
        private bool _disposed;

        public TenantSession(
            TelegramBotConfig config,
            ConversationRepository repository,
            SecretaryBot bot,
            TelegramApiClient telegramApiClient,
            ILogger logger)
        {
            _config = CloneConfig(config);
            _telegramApiClient = telegramApiClient;
            _logger = logger;

            _dispatcher = new MessagePoolingDispatcher(
                repository,
                bot,
                HandleBotResponseAsync);
        }

        public void UpdateConfig(TelegramBotConfig config)
        {
            lock (_sync)
            {
                _config = CloneConfig(config);
            }
        }

        public async Task EnqueueAsync(long chatId, string text)
        {
            TelegramBotConfig snapshot;
            lock (_sync)
            {
                snapshot = CloneConfig(_config);
            }

            var from = BuildPhoneKey(chatId);
            await _dispatcher.EnqueueAsync(snapshot.TenantId, from, text);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _dispatcher.DisposeAsync();
        }

        private async Task HandleBotResponseAsync(BotBatchResult result)
        {
            TelegramBotConfig snapshot;
            lock (_sync)
            {
                snapshot = CloneConfig(_config);
            }

            if (string.IsNullOrWhiteSpace(snapshot.BotToken))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(result.BotText))
            {
                return;
            }

            if (!TryParseChatId(result.Phone, out var chatId))
            {
                return;
            }

            var chunks = SplitMessage(result.BotText, 3500);
            foreach (var chunk in chunks)
            {
                var response = await _telegramApiClient.SendMessageAsync(
                    snapshot.BotToken,
                    chatId,
                    chunk,
                    CancellationToken.None);

                if (!response.Ok)
                {
                    _logger.LogWarning("Falha ao enviar mensagem Telegram tenant={Tenant} chat={ChatId}. code={Code} desc={Desc}",
                        snapshot.TenantId,
                        chatId,
                        response.ErrorCode,
                        response.Description);
                }
            }
        }

        private static string BuildPhoneKey(long chatId)
            => $"tg:{chatId.ToString(CultureInfo.InvariantCulture)}";

        private static bool TryParseChatId(string from, out long chatId)
        {
            chatId = 0;
            if (string.IsNullOrWhiteSpace(from))
            {
                return false;
            }

            const string prefix = "tg:";
            if (!from.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return long.TryParse(
                from[prefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out chatId);
        }

        private static IReadOnlyList<string> SplitMessage(string text, int maxLength)
        {
            var safe = text ?? string.Empty;
            if (safe.Length <= maxLength)
            {
                return new[] { safe };
            }

            var chunks = new List<string>();
            var lines = safe.Split('\n');
            var current = string.Empty;

            foreach (var line in lines)
            {
                var candidate = string.IsNullOrEmpty(current) ? line : $"{current}\n{line}";
                if (candidate.Length <= maxLength)
                {
                    current = candidate;
                    continue;
                }

                if (!string.IsNullOrEmpty(current))
                {
                    chunks.Add(current);
                    current = string.Empty;
                }

                if (line.Length <= maxLength)
                {
                    current = line;
                    continue;
                }

                var start = 0;
                while (start < line.Length)
                {
                    var len = Math.Min(maxLength, line.Length - start);
                    chunks.Add(line.Substring(start, len));
                    start += len;
                }
            }

            if (!string.IsNullOrEmpty(current))
            {
                chunks.Add(current);
            }

            return chunks.Count == 0 ? new[] { safe } : chunks;
        }

        private static TelegramBotConfig CloneConfig(TelegramBotConfig input)
        {
            return new TelegramBotConfig
            {
                TenantId = input.TenantId,
                BotId = input.BotId,
                BotUsername = input.BotUsername,
                BotToken = input.BotToken,
                IsActive = input.IsActive,
                PollingTimeoutSeconds = input.PollingTimeoutSeconds,
                LastUpdateId = input.LastUpdateId,
                UpdatedAtUtc = input.UpdatedAtUtc
            };
        }
    }
}
