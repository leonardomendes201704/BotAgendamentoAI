using System.Collections.Concurrent;
using BotAgendamentoAI.Data;
using BotAgendamentoAI.Domain;

namespace BotAgendamentoAI.Bot;

public sealed record BotBatchResult(
    string TenantId,
    string Phone,
    string UserText,
    string BotText,
    int MessageCount
);

public sealed class MessagePoolingDispatcher : IAsyncDisposable
{
    private readonly ConversationRepository _repository;
    private readonly SecretaryBot _bot;
    private readonly Func<BotBatchResult, Task> _onBotResponse;
    private readonly ConcurrentDictionary<string, ConversationBuffer> _buffers = new(StringComparer.Ordinal);

    public MessagePoolingDispatcher(
        ConversationRepository repository,
        SecretaryBot bot,
        Func<BotBatchResult, Task> onBotResponse)
    {
        _repository = repository;
        _bot = bot;
        _onBotResponse = onBotResponse;
    }

    public async Task<int> EnqueueAsync(string tenantId, string phone, string text)
    {
        var tenant = NormalizeTenant(tenantId);
        var normalizedPhone = NormalizePhone(phone);
        var normalizedText = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return 0;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        await _repository.AddMessage(new ConversationMessage
        {
            TenantId = tenant,
            Phone = normalizedPhone,
            Direction = "in",
            Role = "user",
            Content = normalizedText,
            CreatedAtUtc = nowUtc
        });

        var botConfig = await _repository.GetBotTextConfig(tenant);
        var poolingSeconds = Math.Clamp(botConfig.MessagePoolingSeconds, 0, 120);

        var buffer = _buffers.GetOrAdd(
            BuildConversationKey(tenant, normalizedPhone),
            _ => new ConversationBuffer(tenant, normalizedPhone, _bot, _onBotResponse));

        buffer.Enqueue(normalizedText, poolingSeconds);
        return poolingSeconds;
    }

    public async Task FlushAllAsync()
    {
        foreach (var buffer in _buffers.Values)
        {
            await buffer.FlushNowAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAllAsync();

        foreach (var buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
    }

    private static string BuildConversationKey(string tenantId, string phone)
        => $"{tenantId}|{phone}";

    private static string NormalizeTenant(string tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

    private static string NormalizePhone(string phone)
        => string.IsNullOrWhiteSpace(phone) ? "5511999999999" : phone.Trim();

    private sealed class ConversationBuffer : IDisposable
    {
        private readonly string _tenantId;
        private readonly string _phone;
        private readonly SecretaryBot _bot;
        private readonly Func<BotBatchResult, Task> _onBotResponse;
        private readonly object _sync = new();
        private readonly List<string> _pendingMessages = new();
        private readonly SemaphoreSlim _processingLock = new(1, 1);

        private CancellationTokenSource? _delayCts;
        private bool _disposed;

        public ConversationBuffer(
            string tenantId,
            string phone,
            SecretaryBot bot,
            Func<BotBatchResult, Task> onBotResponse)
        {
            _tenantId = tenantId;
            _phone = phone;
            _bot = bot;
            _onBotResponse = onBotResponse;
        }

        public void Enqueue(string text, int poolingSeconds)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingMessages.Add(text);
                RescheduleLocked(poolingSeconds);
            }
        }

        public async Task FlushNowAsync()
        {
            CancellationTokenSource? previous;
            lock (_sync)
            {
                previous = _delayCts;
                _delayCts = null;
            }

            if (previous is not null)
            {
                previous.Cancel();
                previous.Dispose();
            }

            await FlushPendingAsync(expectedSchedule: null);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _delayCts?.Cancel();
                _delayCts?.Dispose();
                _delayCts = null;
                _pendingMessages.Clear();
            }

            _processingLock.Dispose();
        }

        private void RescheduleLocked(int poolingSeconds)
        {
            _delayCts?.Cancel();
            _delayCts?.Dispose();

            var cts = new CancellationTokenSource();
            _delayCts = cts;
            _ = RunDelayThenFlushAsync(cts, poolingSeconds);
        }

        private async Task RunDelayThenFlushAsync(CancellationTokenSource scheduleCts, int poolingSeconds)
        {
            try
            {
                if (poolingSeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(poolingSeconds), scheduleCts.Token);
                }

                await FlushPendingAsync(scheduleCts);
            }
            catch (OperationCanceledException)
            {
                // Ignore canceled schedules.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[pooling-error] {_tenantId}/{_phone}: {ex.Message}");
            }
            finally
            {
                scheduleCts.Dispose();
            }
        }

        private async Task FlushPendingAsync(CancellationTokenSource? expectedSchedule)
        {
            List<string> snapshot;

            lock (_sync)
            {
                if (_disposed || _pendingMessages.Count == 0)
                {
                    return;
                }

                if (expectedSchedule is not null && !ReferenceEquals(_delayCts, expectedSchedule))
                {
                    return;
                }

                _delayCts = null;
                snapshot = new List<string>(_pendingMessages);
                _pendingMessages.Clear();
            }

            var messages = snapshot
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message.Trim())
                .ToList();

            if (messages.Count == 0)
            {
                return;
            }

            var mergedText = string.Join("\n", messages);

            await _processingLock.WaitAsync();
            try
            {
                var incoming = new IncomingMessage(_tenantId, _phone, mergedText)
                {
                    UserMessageAlreadyPersisted = true,
                    BatchedUserMessages = messages
                };

                var answer = await _bot.HandleAsync(incoming);
                await _onBotResponse(new BotBatchResult(_tenantId, _phone, mergedText, answer, messages.Count));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[pooling-process-error] {_tenantId}/{_phone}: {ex.Message}");
                await _onBotResponse(new BotBatchResult(
                    _tenantId,
                    _phone,
                    mergedText,
                    "Desculpe, houve um erro temporario. Pode tentar novamente em instantes?",
                    messages.Count));
            }
            finally
            {
                _processingLock.Release();
            }
        }
    }
}
