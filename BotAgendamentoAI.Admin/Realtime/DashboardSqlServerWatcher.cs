using BotAgendamentoAI.Admin.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace BotAgendamentoAI.Admin.Realtime;

public sealed class DashboardSqlServerWatcher : BackgroundService
{
    private readonly IDashboardRealtimeNotifier _notifier;
    private readonly ILogger<DashboardSqlServerWatcher> _logger;
    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    private Dictionary<string, TenantWatermark> _previous = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public DashboardSqlServerWatcher(
        IOptions<AdminOptions> options,
        IDashboardRealtimeNotifier notifier,
        ILogger<DashboardSqlServerWatcher> logger)
    {
        _notifier = notifier;
        _logger = logger;

        _connectionString = ResolveConnectionString(options.Value.ConnectionString);

        var configuredSeconds = options.Value.DashboardRealtimePollSeconds;
        _pollInterval = TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 1, 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Dashboard SQL Server watcher started. Interval: {IntervalSeconds}s",
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
                _logger.LogWarning(ex, "Dashboard SQL Server watcher loop failed.");
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
                SELECT tenant_id, COUNT_BIG(*)
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

        return output;
    }

    private static async Task MergeLongWatermarksAsync(
        SqlConnection connection,
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
        SqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
              AND TABLE_NAME = @table_name;
            """;
        command.Parameters.AddWithValue("@table_name", tableName);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is not null && scalar is not DBNull;
    }

    private static async Task MergeTextWatermarksAsync(
        SqlConnection connection,
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
            var value = reader.IsDBNull(1) ? string.Empty : ToInvariantText(reader.GetValue(1));
            var watermark = GetOrCreate(destination, tenantId);
            apply(watermark, value);
        }
    }

    private SqlConnection CreateConnection() => new(_connectionString);

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

    private static string ToInvariantText(object? value)
    {
        if (value is null or DBNull)
        {
            return string.Empty;
        }

        return value switch
        {
            string s => s,
            DateTimeOffset offset => offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => (dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime)
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string ResolveConnectionString(string? configuredConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString.Trim();
        }

        var envValue = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue.Trim();
        }

        throw new InvalidOperationException("Connection string not configured for SQL Server dashboard watcher.");
    }

    private sealed class TenantWatermark
    {
        public long MaxLegacyMessageId { get; set; }
        public long MaxTelegramMessageId { get; set; }
        public long MaxBookingRowId { get; set; }
        public long MaxJobId { get; set; }
        public string MaxStateUpdatedAtUtc { get; set; } = string.Empty;
        public string MaxSessionUpdatedAtUtc { get; set; } = string.Empty;

        public bool IsNewerThan(TenantWatermark? previous)
        {
            if (previous is null)
            {
                return MaxLegacyMessageId > 0
                       || MaxTelegramMessageId > 0
                       || MaxBookingRowId > 0
                       || MaxJobId > 0
                       || !string.IsNullOrWhiteSpace(MaxStateUpdatedAtUtc)
                       || !string.IsNullOrWhiteSpace(MaxSessionUpdatedAtUtc);
            }

            return MaxLegacyMessageId > previous.MaxLegacyMessageId
                   || MaxTelegramMessageId > previous.MaxTelegramMessageId
                   || MaxBookingRowId > previous.MaxBookingRowId
                   || MaxJobId > previous.MaxJobId
                   || string.CompareOrdinal(MaxStateUpdatedAtUtc, previous.MaxStateUpdatedAtUtc) > 0
                   || string.CompareOrdinal(MaxSessionUpdatedAtUtc, previous.MaxSessionUpdatedAtUtc) > 0;
        }
    }
}


