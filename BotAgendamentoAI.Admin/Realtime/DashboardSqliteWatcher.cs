using BotAgendamentoAI.Admin.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BotAgendamentoAI.Admin.Realtime;

public sealed class DashboardSqliteWatcher : BackgroundService
{
    private readonly IDashboardRealtimeNotifier _notifier;
    private readonly ILogger<DashboardSqliteWatcher> _logger;
    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    private Dictionary<string, TenantWatermark> _previous = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public DashboardSqliteWatcher(
        IOptions<AdminOptions> options,
        IDashboardRealtimeNotifier notifier,
        ILogger<DashboardSqliteWatcher> logger)
    {
        _notifier = notifier;
        _logger = logger;

        var dbPath = ResolveDatabasePath(options.Value.DatabasePath);
        _connectionString = $"Data Source={dbPath}";

        var configuredSeconds = options.Value.DashboardRealtimePollSeconds;
        _pollInterval = TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 1, 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Dashboard SQLite watcher started. Interval: {IntervalSeconds}s",
            _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var current = await ReadWatermarksAsync(stoppingToken);
                if (_initialized)
                {
                    await NotifyChangedTenantsAsync(current, stoppingToken);
                }
                else
                {
                    _initialized = true;
                }

                _previous = current;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dashboard SQLite watcher loop failed.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task NotifyChangedTenantsAsync(
        Dictionary<string, TenantWatermark> current,
        CancellationToken cancellationToken)
    {
        foreach (var (tenantId, watermark) in current)
        {
            _previous.TryGetValue(tenantId, out var previous);
            if (!watermark.IsNewerThan(previous))
            {
                continue;
            }

            await _notifier.NotifyTenantChangedAsync(tenantId, cancellationToken);
        }
    }

    private async Task<Dictionary<string, TenantWatermark>> ReadWatermarksAsync(CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, TenantWatermark>(StringComparer.OrdinalIgnoreCase);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await MergeLongWatermarksAsync(
            connection,
            """
            SELECT tenant_id, MAX(id)
            FROM conversation_messages
            GROUP BY tenant_id;
            """,
            output,
            static (watermark, value) => watermark.MaxMessageId = value,
            cancellationToken);

        await MergeLongWatermarksAsync(
            connection,
            """
            SELECT tenant_id, MAX(rowid)
            FROM bookings
            GROUP BY tenant_id;
            """,
            output,
            static (watermark, value) => watermark.MaxBookingRowId = value,
            cancellationToken);

        await MergeTextWatermarksAsync(
            connection,
            """
            SELECT tenant_id, MAX(updated_at_utc)
            FROM conversation_state
            GROUP BY tenant_id;
            """,
            output,
            static (watermark, value) => watermark.MaxStateUpdatedAtUtc = value,
            cancellationToken);

        return output;
    }

    private static async Task MergeLongWatermarksAsync(
        SqliteConnection connection,
        string sql,
        Dictionary<string, TenantWatermark> destination,
        Action<TenantWatermark, long> apply,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tenantId = NormalizeTenant(reader.IsDBNull(0) ? null : reader.GetString(0));
            var value = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
            var watermark = GetOrCreate(destination, tenantId);
            apply(watermark, value);
        }
    }

    private static async Task MergeTextWatermarksAsync(
        SqliteConnection connection,
        string sql,
        Dictionary<string, TenantWatermark> destination,
        Action<TenantWatermark, string> apply,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tenantId = NormalizeTenant(reader.IsDBNull(0) ? null : reader.GetString(0));
            var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var watermark = GetOrCreate(destination, tenantId);
            apply(watermark, value);
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static TenantWatermark GetOrCreate(
        Dictionary<string, TenantWatermark> source,
        string tenantId)
    {
        if (source.TryGetValue(tenantId, out var existing))
        {
            return existing;
        }

        var created = new TenantWatermark();
        source[tenantId] = created;
        return created;
    }

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

    private static string ResolveDatabasePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var envPath = Environment.GetEnvironmentVariable("BOT_DB_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "bot.db")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Debug", "net9.0", "data", "bot.db")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bin", "Debug", "net9.0", "data", "bot.db"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private sealed class TenantWatermark
    {
        public long MaxMessageId { get; set; }
        public long MaxBookingRowId { get; set; }
        public string MaxStateUpdatedAtUtc { get; set; } = string.Empty;

        public bool IsNewerThan(TenantWatermark? previous)
        {
            if (previous is null)
            {
                return MaxMessageId > 0 || MaxBookingRowId > 0 || !string.IsNullOrWhiteSpace(MaxStateUpdatedAtUtc);
            }

            return MaxMessageId > previous.MaxMessageId
                   || MaxBookingRowId > previous.MaxBookingRowId
                   || string.CompareOrdinal(MaxStateUpdatedAtUtc, previous.MaxStateUpdatedAtUtc) > 0;
        }
    }
}
