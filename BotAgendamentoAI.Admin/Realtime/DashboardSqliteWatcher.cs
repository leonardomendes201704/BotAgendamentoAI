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

        if (await TableExistsAsync(connection, "tg_conversation_messages", cancellationToken))
        {
            await MergeLongWatermarksAsync(
                connection,
                """
                SELECT tenant_id, MAX(id)
                FROM tg_conversation_messages
                GROUP BY tenant_id;
                """,
                output,
                static (watermark, value) => watermark.MaxLegacyMessageId = value,
                cancellationToken);
        }

        if (await TableExistsAsync(connection, "tg_MessagesLog", cancellationToken))
        {
            await MergeLongWatermarksAsync(
                connection,
                """
                SELECT TenantId, MAX(Id)
                FROM tg_MessagesLog
                GROUP BY TenantId;
                """,
                output,
                static (watermark, value) => watermark.MaxTelegramMessageId = value,
                cancellationToken);
        }

        if (await TableExistsAsync(connection, "tg_bookings", cancellationToken))
        {
            await MergeLongWatermarksAsync(
                connection,
                """
                SELECT tenant_id, MAX(rowid)
                FROM tg_bookings
                GROUP BY tenant_id;
                """,
                output,
                static (watermark, value) => watermark.MaxBookingRowId = value,
                cancellationToken);
        }

        if (await TableExistsAsync(connection, "tg_Jobs", cancellationToken))
        {
            await MergeLongWatermarksAsync(
                connection,
                """
                SELECT TenantId, MAX(Id)
                FROM tg_Jobs
                GROUP BY TenantId;
                """,
                output,
                static (watermark, value) => watermark.MaxJobId = value,
                cancellationToken);
        }

        if (await TableExistsAsync(connection, "tg_conversation_state", cancellationToken))
        {
            await MergeTextWatermarksAsync(
                connection,
                """
                SELECT tenant_id, MAX(updated_at_utc)
                FROM tg_conversation_state
                GROUP BY tenant_id;
                """,
                output,
                static (watermark, value) => watermark.MaxStateUpdatedAtUtc = value,
                cancellationToken);
        }

        if (await TableExistsAsync(connection, "tg_UserSessions", cancellationToken)
            && await TableExistsAsync(connection, "tg_Users", cancellationToken))
        {
            await MergeTextWatermarksAsync(
                connection,
                """
                SELECT u.TenantId, MAX(s.UpdatedAt)
                FROM tg_UserSessions s
                INNER JOIN tg_Users u ON u.Id = s.UserId
                GROUP BY u.TenantId;
                """,
                output,
                static (watermark, value) => watermark.MaxSessionUpdatedAtUtc = value,
                cancellationToken);
        }

        if (await TableExistsAsync(connection, "tg_human_handoff_sessions", cancellationToken))
        {
            await MergeTextWatermarksAsync(
                connection,
                """
                SELECT tenant_id, MAX(last_message_at_utc)
                FROM tg_human_handoff_sessions
                GROUP BY tenant_id;
                """,
                output,
                static (watermark, value) => watermark.MaxHandoffUpdatedAtUtc = value,
                cancellationToken);
        }

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

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = @table_name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@table_name", tableName);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is not null && scalar is not DBNull;
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

        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "bin", "Debug", "net9.0", "data", "bot.db"));
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return path;
    }

    private sealed class TenantWatermark
    {
        public long MaxLegacyMessageId { get; set; }
        public long MaxTelegramMessageId { get; set; }
        public long MaxBookingRowId { get; set; }
        public long MaxJobId { get; set; }
        public string MaxStateUpdatedAtUtc { get; set; } = string.Empty;
        public string MaxSessionUpdatedAtUtc { get; set; } = string.Empty;
        public string MaxHandoffUpdatedAtUtc { get; set; } = string.Empty;

        public bool IsNewerThan(TenantWatermark? previous)
        {
            if (previous is null)
            {
                return MaxLegacyMessageId > 0
                       || MaxTelegramMessageId > 0
                       || MaxBookingRowId > 0
                       || MaxJobId > 0
                       || !string.IsNullOrWhiteSpace(MaxStateUpdatedAtUtc)
                       || !string.IsNullOrWhiteSpace(MaxSessionUpdatedAtUtc)
                       || !string.IsNullOrWhiteSpace(MaxHandoffUpdatedAtUtc);
            }

            return MaxLegacyMessageId > previous.MaxLegacyMessageId
                   || MaxTelegramMessageId > previous.MaxTelegramMessageId
                   || MaxBookingRowId > previous.MaxBookingRowId
                   || MaxJobId > previous.MaxJobId
                   || string.CompareOrdinal(MaxStateUpdatedAtUtc, previous.MaxStateUpdatedAtUtc) > 0
                   || string.CompareOrdinal(MaxSessionUpdatedAtUtc, previous.MaxSessionUpdatedAtUtc) > 0
                   || string.CompareOrdinal(MaxHandoffUpdatedAtUtc, previous.MaxHandoffUpdatedAtUtc) > 0;
        }
    }
}

