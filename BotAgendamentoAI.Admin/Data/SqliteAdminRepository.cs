using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotAgendamentoAI.Admin.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BotAgendamentoAI.Admin.Data;

public sealed class SqliteAdminRepository : IAdminRepository
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteAdminRepository> _logger;
    private static readonly HttpClient GeocodeHttpClient = BuildGeocodeHttpClient();
    private static readonly HttpClient TelegramHttpClient = BuildTelegramHttpClient();
    private static readonly ConcurrentDictionary<string, string> ReverseNeighborhoodCache = new(StringComparer.Ordinal);
    private const int CoverageReverseLookupBudget = 120;

    private const string AdminSchemaSql = """
CREATE TABLE IF NOT EXISTS tg_conversation_messages (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  tenant_id TEXT NOT NULL,
  phone TEXT NOT NULL,
  direction TEXT NOT NULL,
  role TEXT NOT NULL,
  content TEXT NOT NULL,
  tool_name TEXT NULL,
  tool_call_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  metadata_json TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_conversation_messages_tenant_phone_created
ON tg_conversation_messages(tenant_id, phone, created_at_utc);

CREATE TABLE IF NOT EXISTS tg_conversation_state (
  tenant_id TEXT NOT NULL,
  phone TEXT NOT NULL,
  summary TEXT NOT NULL,
  slots_json TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY (tenant_id, phone)
);

CREATE TABLE IF NOT EXISTS tg_service_categories (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  tenant_id TEXT NOT NULL,
  name TEXT NOT NULL,
  normalized_name TEXT NOT NULL,
  created_at_utc TEXT NOT NULL,
  UNIQUE(tenant_id, normalized_name)
);

CREATE INDEX IF NOT EXISTS idx_service_categories_tenant_name
ON tg_service_categories(tenant_id, name);

CREATE TABLE IF NOT EXISTS tg_bookings (
  id TEXT PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  customer_phone TEXT NOT NULL,
  customer_name TEXT NOT NULL,
  service_category TEXT NOT NULL,
  service_title TEXT NOT NULL,
  start_local TEXT NOT NULL,
  duration_minutes INTEGER NOT NULL,
  address TEXT NOT NULL,
  notes TEXT NOT NULL,
  technician_name TEXT NOT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_bookings_tenant_phone_start
ON tg_bookings(tenant_id, customer_phone, start_local);

CREATE INDEX IF NOT EXISTS idx_bookings_tenant_start
ON tg_bookings(tenant_id, start_local);

CREATE TABLE IF NOT EXISTS tg_booking_geocode_cache (
  booking_id TEXT PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  address TEXT NOT NULL,
  latitude REAL NULL,
  longitude REAL NULL,
  status TEXT NOT NULL,
  error_message TEXT NULL,
  geocoded_at_utc TEXT NOT NULL,
  retry_after_utc TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_booking_geocode_cache_tenant_status
ON tg_booking_geocode_cache(tenant_id, status, retry_after_utc);

CREATE TABLE IF NOT EXISTS tg_service_catalog (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  tenant_id TEXT NOT NULL,
  title TEXT NOT NULL,
  category_name TEXT NOT NULL,
  default_duration_minutes INTEGER NOT NULL DEFAULT 60,
  is_active INTEGER NOT NULL DEFAULT 1,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_service_catalog_tenant_active
ON tg_service_catalog(tenant_id, is_active, title);

CREATE TABLE IF NOT EXISTS tg_tenant_bot_config (
  tenant_id TEXT PRIMARY KEY,
  menu_json TEXT NOT NULL,
  messages_json TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tg_tenant_telegram_config (
  tenant_id TEXT PRIMARY KEY,
  bot_id TEXT NOT NULL,
  bot_username TEXT NOT NULL,
  bot_token TEXT NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 0,
  polling_timeout_seconds INTEGER NOT NULL DEFAULT 30,
  last_update_id INTEGER NOT NULL DEFAULT 0,
  updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tg_tenant_google_calendar_config (
  tenant_id TEXT PRIMARY KEY,
  is_enabled INTEGER NOT NULL DEFAULT 0,
  calendar_id TEXT NOT NULL DEFAULT '',
  service_account_json TEXT NOT NULL DEFAULT '',
  time_zone_id TEXT NOT NULL DEFAULT 'America/Sao_Paulo',
  default_duration_minutes INTEGER NOT NULL DEFAULT 60,
  availability_window_days INTEGER NOT NULL DEFAULT 7,
  availability_slot_interval_minutes INTEGER NOT NULL DEFAULT 60,
  availability_workday_start_hour INTEGER NOT NULL DEFAULT 8,
  availability_workday_end_hour INTEGER NOT NULL DEFAULT 20,
  availability_today_lead_minutes INTEGER NOT NULL DEFAULT 30,
  max_attempts INTEGER NOT NULL DEFAULT 8,
  retry_base_seconds INTEGER NOT NULL DEFAULT 10,
  retry_max_seconds INTEGER NOT NULL DEFAULT 600,
  event_title_template TEXT NOT NULL DEFAULT '',
  event_description_template TEXT NOT NULL DEFAULT '',
  updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tg_human_handoff_sessions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  tenant_id TEXT NOT NULL,
  telegram_user_id INTEGER NOT NULL,
  app_user_id INTEGER NULL,
  requested_by_role TEXT NOT NULL DEFAULT 'unknown',
  is_open INTEGER NOT NULL DEFAULT 1,
  requested_at_utc TEXT NOT NULL,
  accepted_at_utc TEXT NULL,
  closed_at_utc TEXT NULL,
  assigned_agent TEXT NULL,
  previous_state TEXT NULL,
  close_reason TEXT NULL,
  last_message_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_tg_human_handoff_sessions_tenant_open_requested
ON tg_human_handoff_sessions(tenant_id, is_open, requested_at_utc DESC);

CREATE UNIQUE INDEX IF NOT EXISTS uq_tg_human_handoff_sessions_open_thread
ON tg_human_handoff_sessions(tenant_id, telegram_user_id)
WHERE is_open = 1;

CREATE TABLE IF NOT EXISTS tg_client_profiles (
  user_id INTEGER PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  full_name TEXT NOT NULL DEFAULT '',
  email TEXT NOT NULL DEFAULT '',
  cpf TEXT NOT NULL DEFAULT '',
  street TEXT NOT NULL DEFAULT '',
  number TEXT NOT NULL DEFAULT '',
  complement TEXT NOT NULL DEFAULT '',
  neighborhood TEXT NOT NULL DEFAULT '',
  city TEXT NOT NULL DEFAULT '',
  state TEXT NOT NULL DEFAULT '',
  cep TEXT NOT NULL DEFAULT '',
  latitude REAL NULL,
  longitude REAL NULL,
  is_address_confirmed INTEGER NOT NULL DEFAULT 0,
  phone_number TEXT NOT NULL DEFAULT '',
  is_registration_complete INTEGER NOT NULL DEFAULT 0,
  created_at_utc TEXT NOT NULL DEFAULT '',
  updated_at_utc TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS ix_tg_client_profiles_tenant_complete_updated
ON tg_client_profiles(tenant_id, is_registration_complete, updated_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_tg_client_profiles_tenant_cpf
ON tg_client_profiles(tenant_id, cpf);

CREATE INDEX IF NOT EXISTS ix_tg_client_profiles_tenant_phone
ON tg_client_profiles(tenant_id, phone_number);

CREATE TABLE IF NOT EXISTS tg_shared_settings (
  setting_key TEXT PRIMARY KEY,
  setting_value TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL
);
""";

    public SqliteAdminRepository(IOptions<AdminOptions> options, ILogger<SqliteAdminRepository> logger)
    {
        _logger = logger;
        _dbPath = ResolveDatabasePath(options.Value.DatabasePath);
        _connectionString = $"Data Source={_dbPath}";
    }

    public async Task InitializeAsync()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = AdminSchemaSql;
        await command.ExecuteNonQueryAsync();

        await EnsureColumnAsync(connection, "tg_tenant_google_calendar_config", "availability_window_days", "INTEGER NOT NULL DEFAULT 7");
        await EnsureColumnAsync(connection, "tg_tenant_google_calendar_config", "availability_slot_interval_minutes", "INTEGER NOT NULL DEFAULT 60");
        await EnsureColumnAsync(connection, "tg_tenant_google_calendar_config", "availability_workday_start_hour", "INTEGER NOT NULL DEFAULT 8");
        await EnsureColumnAsync(connection, "tg_tenant_google_calendar_config", "availability_workday_end_hour", "INTEGER NOT NULL DEFAULT 20");
        await EnsureColumnAsync(connection, "tg_tenant_google_calendar_config", "availability_today_lead_minutes", "INTEGER NOT NULL DEFAULT 30");
        await EnsureColumnAsync(connection, "tg_tenant_google_calendar_config", "max_attempts", "INTEGER NOT NULL DEFAULT 8");
        await EnsureColumnAsync(connection, "tg_tenant_google_calendar_config", "retry_base_seconds", "INTEGER NOT NULL DEFAULT 10");
        await EnsureColumnAsync(connection, "tg_tenant_google_calendar_config", "retry_max_seconds", "INTEGER NOT NULL DEFAULT 600");

        _logger.LogInformation("Admin SQLite path: {Path}", _dbPath);
    }

    public async Task<IReadOnlyList<string>> GetTenantIdsAsync()
    {
        var tenants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        async Task CollectFromTableAsync(string tableName, string tenantColumn)
        {
            if (!await TableExistsAsync(connection, tableName))
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT {tenantColumn} FROM {tableName};";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    var value = reader.GetString(0).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        tenants.Add(value);
                    }
                }
            }
        }

        await CollectFromTableAsync("tg_conversation_messages", "tenant_id");
        await CollectFromTableAsync("tg_bookings", "tenant_id");
        await CollectFromTableAsync("tg_conversation_state", "tenant_id");
        await CollectFromTableAsync("tg_tenant_bot_config", "tenant_id");
        await CollectFromTableAsync("tg_tenant_telegram_config", "tenant_id");
        await CollectFromTableAsync("tg_tenant_google_calendar_config", "tenant_id");
        await CollectFromTableAsync("tg_client_profiles", "tenant_id");
        await CollectFromTableAsync("tg_Users", "TenantId");
        await CollectFromTableAsync("tg_MessagesLog", "TenantId");
        await CollectFromTableAsync("tg_human_handoff_sessions", "tenant_id");

        if (tenants.Count == 0)
        {
            tenants.Add("A");
        }

        return tenants.OrderBy(x => x).ToList();
    }

    public async Task<DashboardViewModel> GetDashboardAsync(string tenantId, int days)
    {
        var tenant = NormalizeTenant(tenantId);
        var safeDays = Math.Clamp(days, 1, 365);
        var nowUtc = DateTimeOffset.UtcNow;
        var fromUtc = nowUtc.AddDays(-safeDays);
        var fromText = ToUtcText(fromUtc);
        var nowText = ToUtcText(nowUtc);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasLegacyMessages = await TableExistsAsync(connection, "tg_conversation_messages");
        var hasMessagesLog = await TableExistsAsync(connection, "tg_MessagesLog");
        var hasLegacyBookings = await TableExistsAsync(connection, "tg_bookings");
        var hasJobs = await TableExistsAsync(connection, "tg_Jobs");
        var hasUsers = await TableExistsAsync(connection, "tg_Users");
        var hasLegacyState = await TableExistsAsync(connection, "tg_conversation_state");
        var hasHandoffSessions = await TableExistsAsync(connection, "tg_human_handoff_sessions");
        var hasProviderJobRejections = await TableExistsAsync(connection, "tg_provider_job_rejections");

        var incomingConversationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var convertedConversationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalMessages = 0;
        var createdBookings = 0;
        var finishedBookings = 0;
        var cancelledBookings = 0;
        var rejectedJobs = 0;

        if (hasLegacyMessages)
        {
            totalMessages += await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_conversation_messages
                WHERE tenant_id = @tenant_id
                  AND created_at_utc >= @from_utc
                  AND created_at_utc <= @to_utc;
                """,
                ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

            await using var inboundLegacyCommand = connection.CreateCommand();
            inboundLegacyCommand.CommandText =
                """
                SELECT DISTINCT phone
                FROM tg_conversation_messages
                WHERE tenant_id = @tenant_id
                  AND role = 'user'
                  AND lower(direction) = 'in'
                  AND created_at_utc >= @from_utc
                  AND created_at_utc <= @to_utc;
                """;
            inboundLegacyCommand.Parameters.AddWithValue("@tenant_id", tenant);
            inboundLegacyCommand.Parameters.AddWithValue("@from_utc", fromText);
            inboundLegacyCommand.Parameters.AddWithValue("@to_utc", nowText);

            await using var reader = await inboundLegacyCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    incomingConversationKeys.Add($"legacy:{reader.GetString(0)}");
                }
            }
        }

        if (hasMessagesLog)
        {
            totalMessages += await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_MessagesLog
                WHERE TenantId = @tenant_id
                  AND CreatedAt >= @from_utc
                  AND CreatedAt <= @to_utc;
                """,
                ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

            await using var inboundTelegramCommand = connection.CreateCommand();
            inboundTelegramCommand.CommandText =
                """
                SELECT DISTINCT TelegramUserId
                FROM tg_MessagesLog
                WHERE TenantId = @tenant_id
                  AND lower(Direction) = 'in'
                  AND CreatedAt >= @from_utc
                  AND CreatedAt <= @to_utc;
                """;
            inboundTelegramCommand.Parameters.AddWithValue("@tenant_id", tenant);
            inboundTelegramCommand.Parameters.AddWithValue("@from_utc", fromText);
            inboundTelegramCommand.Parameters.AddWithValue("@to_utc", nowText);

            await using var reader = await inboundTelegramCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    incomingConversationKeys.Add($"tg:{reader.GetInt64(0)}");
                }
            }
        }

        if (hasLegacyBookings)
        {
            createdBookings += await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_bookings
                WHERE tenant_id = @tenant_id
                  AND created_at_utc >= @from_utc
                  AND created_at_utc <= @to_utc;
                """,
                ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

            await using var convertedLegacyCommand = connection.CreateCommand();
            convertedLegacyCommand.CommandText =
                """
                SELECT DISTINCT customer_phone
                FROM tg_bookings
                WHERE tenant_id = @tenant_id
                  AND created_at_utc >= @from_utc
                  AND created_at_utc <= @to_utc;
                """;
            convertedLegacyCommand.Parameters.AddWithValue("@tenant_id", tenant);
            convertedLegacyCommand.Parameters.AddWithValue("@from_utc", fromText);
            convertedLegacyCommand.Parameters.AddWithValue("@to_utc", nowText);

            await using var reader = await convertedLegacyCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    convertedConversationKeys.Add($"legacy:{reader.GetString(0)}");
                }
            }
        }

        if (hasJobs)
        {
            createdBookings += await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_Jobs
                WHERE TenantId = @tenant_id
                  AND Status <> 'Draft'
                  AND CreatedAt >= @from_utc
                  AND CreatedAt <= @to_utc;
                """,
                ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

            finishedBookings = await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_Jobs
                WHERE TenantId = @tenant_id
                  AND Status = 'Finished'
                  AND CreatedAt >= @from_utc
                  AND CreatedAt <= @to_utc;
                """,
                ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

            cancelledBookings = await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_Jobs
                WHERE TenantId = @tenant_id
                  AND Status = 'Cancelled'
                  AND CreatedAt >= @from_utc
                  AND CreatedAt <= @to_utc;
                """,
                ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

            await using var convertedJobsCommand = connection.CreateCommand();
            convertedJobsCommand.CommandText = hasUsers
                ? """
                  SELECT DISTINCT COALESCE(u.TelegramUserId, 0), j.ClientUserId
                  FROM tg_Jobs j
                  LEFT JOIN tg_Users u
                    ON u.Id = j.ClientUserId
                   AND u.TenantId = j.TenantId
                  WHERE j.TenantId = @tenant_id
                    AND j.Status <> 'Draft'
                    AND j.CreatedAt >= @from_utc
                    AND j.CreatedAt <= @to_utc;
                  """
                : """
                  SELECT DISTINCT 0, j.ClientUserId
                  FROM tg_Jobs j
                  WHERE j.TenantId = @tenant_id
                    AND j.Status <> 'Draft'
                    AND j.CreatedAt >= @from_utc
                    AND j.CreatedAt <= @to_utc;
                  """;
            convertedJobsCommand.Parameters.AddWithValue("@tenant_id", tenant);
            convertedJobsCommand.Parameters.AddWithValue("@from_utc", fromText);
            convertedJobsCommand.Parameters.AddWithValue("@to_utc", nowText);

            await using var reader = await convertedJobsCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var telegramUserId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                if (telegramUserId > 0)
                {
                    convertedConversationKeys.Add($"tg:{telegramUserId}");
                }
            }
        }

        var humanHandoffOpen = 0;
        if (hasHandoffSessions)
        {
            humanHandoffOpen = await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_human_handoff_sessions
                WHERE tenant_id = @tenant_id
                  AND is_open = 1;
                """,
                ("@tenant_id", tenant));
        }
        else if (hasLegacyState)
        {
            humanHandoffOpen = await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_conversation_state
                WHERE tenant_id = @tenant_id
                  AND (
                    json_extract(slots_json, '$.menuContext') = 'human_handoff'
                    OR json_extract(slots_json, '$.pending') = 'human_handoff'
                  );
                """,
                ("@tenant_id", tenant));
        }

        if (hasProviderJobRejections)
        {
            rejectedJobs = await QueryIntAsync(connection,
                """
                SELECT COUNT(*)
                FROM tg_provider_job_rejections
                WHERE tenant_id = @tenant_id
                  AND created_at_utc >= @from_utc
                  AND created_at_utc <= @to_utc;
                """,
                ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));
        }

        var recentConversations = await GetConversationThreadsAsync(tenant, 20);
        var recentBookings = await GetBookingsAsync(tenant, 20);
        var mapPins = await GetDashboardMapPinsAsync(tenant, null, 300);

        var totalIncomingConversations = incomingConversationKeys.Count;
        var convertedPhones = convertedConversationKeys.Count;
        var conversionRate = totalIncomingConversations == 0
            ? 0m
            : Math.Round(convertedPhones * 100m / totalIncomingConversations, 2);
        createdBookings = Math.Max(0, createdBookings - finishedBookings - cancelledBookings);

        return new DashboardViewModel
        {
            TenantId = tenant,
            Days = safeDays,
            FromUtc = fromUtc,
            ToUtc = nowUtc,
            TotalIncomingConversations = totalIncomingConversations,
            TotalMessages = totalMessages,
            CreatedBookings = createdBookings,
            FinishedBookings = finishedBookings,
            CancelledBookings = cancelledBookings,
            RejectedJobs = rejectedJobs,
            HumanHandoffOpen = humanHandoffOpen,
            ConvertedPhones = convertedPhones,
            ConversionRatePercent = conversionRate,
            RecentConversations = recentConversations,
            RecentBookings = recentBookings,
            MapPins = mapPins
        };
    }

    public async Task<IReadOnlyList<DashboardMapPinItem>> GetDashboardMapPinsAsync(string tenantId, DateTimeOffset? sinceUtc, int limit)
    {
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var nowUtc = DateTimeOffset.UtcNow;
        var rows = new List<DashboardMapRow>();

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var hasLegacyBookings = await TableExistsAsync(connection, "tg_bookings");
        var hasGeocodeCache = await TableExistsAsync(connection, "tg_booking_geocode_cache");
        var hasJobs = await TableExistsAsync(connection, "tg_Jobs");
        var hasUsers = await TableExistsAsync(connection, "tg_Users");

        if (hasLegacyBookings)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = hasGeocodeCache
                ? """
                  SELECT
                    b.id,
                    b.tenant_id,
                    b.service_category,
                    b.service_title,
                    b.customer_phone,
                    b.address,
                    b.start_local,
                    b.created_at_utc,
                    g.latitude,
                    g.longitude,
                    g.status,
                    g.retry_after_utc
                  FROM tg_bookings b
                  LEFT JOIN tg_booking_geocode_cache g ON g.booking_id = b.id
                  WHERE b.tenant_id = @tenant_id
                    AND (@since_utc IS NULL OR b.created_at_utc >= @since_utc)
                  ORDER BY b.created_at_utc DESC
                  LIMIT @limit;
                  """
                : """
                  SELECT
                    b.id,
                    b.tenant_id,
                    b.service_category,
                    b.service_title,
                    b.customer_phone,
                    b.address,
                    b.start_local,
                    b.created_at_utc,
                    NULL,
                    NULL,
                    '',
                    NULL
                  FROM tg_bookings b
                  WHERE b.tenant_id = @tenant_id
                    AND (@since_utc IS NULL OR b.created_at_utc >= @since_utc)
                  ORDER BY b.created_at_utc DESC
                  LIMIT @limit;
                  """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@since_utc", sinceUtc.HasValue ? ToUtcText(sinceUtc.Value) : (object)DBNull.Value);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new DashboardMapRow
                {
                    BookingId = reader.GetString(0),
                    TenantId = reader.GetString(1),
                    ServiceCategory = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    ServiceTitle = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    CustomerPhone = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Address = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    StartLocal = ParseLocalDateTime(reader.IsDBNull(6) ? string.Empty : reader.GetString(6)),
                    CreatedAtUtc = ParseUtc(reader.IsDBNull(7) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(7)),
                    Latitude = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    Longitude = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    GeocodeStatus = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    RetryAfterUtc = TryParseNullableUtc(reader.IsDBNull(11) ? null : reader.GetString(11))
                });
            }
        }

        if (hasJobs)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = hasUsers
                ? """
                  SELECT
                    j.Id,
                    j.TenantId,
                    j.Category,
                    j.Description,
                    u.Phone,
                    u.TelegramUserId,
                    j.ClientUserId,
                    COALESCE(j.AddressText, ''),
                    COALESCE(j.ScheduledAt, j.CreatedAt),
                    j.CreatedAt,
                    j.Latitude,
                    j.Longitude
                  FROM tg_Jobs j
                  LEFT JOIN tg_Users u
                    ON u.Id = j.ClientUserId
                   AND u.TenantId = j.TenantId
                  WHERE j.TenantId = @tenant_id
                    AND j.Status NOT IN ('Draft', 'Finished', 'Cancelled')
                    AND (@since_utc IS NULL OR j.CreatedAt >= @since_utc)
                  ORDER BY j.CreatedAt DESC
                  LIMIT @limit;
                  """
                : """
                  SELECT
                    j.Id,
                    j.TenantId,
                    j.Category,
                    j.Description,
                    NULL,
                    NULL,
                    j.ClientUserId,
                    COALESCE(j.AddressText, ''),
                    COALESCE(j.ScheduledAt, j.CreatedAt),
                    j.CreatedAt,
                    j.Latitude,
                    j.Longitude
                  FROM tg_Jobs j
                  WHERE j.TenantId = @tenant_id
                    AND j.Status NOT IN ('Draft', 'Finished', 'Cancelled')
                    AND (@since_utc IS NULL OR j.CreatedAt >= @since_utc)
                  ORDER BY j.CreatedAt DESC
                  LIMIT @limit;
                  """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@since_utc", sinceUtc.HasValue ? ToUtcText(sinceUtc.Value) : (object)DBNull.Value);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var telegramUserId = reader.IsDBNull(5) ? 0L : reader.GetInt64(5);
                var clientUserId = reader.IsDBNull(6) ? 0L : reader.GetInt64(6);
                var customerPhone = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                if (string.IsNullOrWhiteSpace(customerPhone))
                {
                    customerPhone = telegramUserId > 0 ? $"tg:{telegramUserId}" : $"user:{clientUserId}";
                }

                var scheduledRaw = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
                var createdAtRaw = reader.IsDBNull(9) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(9);
                var createdAtUtc = ParseUtc(createdAtRaw);

                var row = new DashboardMapRow
                {
                    BookingId = $"job:{reader.GetInt64(0)}",
                    TenantId = reader.IsDBNull(1) ? tenant : reader.GetString(1),
                    ServiceCategory = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    ServiceTitle = TrimPreview(reader.IsDBNull(3) ? string.Empty : reader.GetString(3)),
                    CustomerPhone = customerPhone,
                    Address = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    StartLocal = ParseLocalOrUtcDateTime(scheduledRaw, createdAtUtc),
                    CreatedAtUtc = createdAtUtc,
                    Latitude = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    Longitude = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    GeocodeStatus = string.Empty,
                    RetryAfterUtc = null
                };

                if ((!row.Latitude.HasValue || !row.Longitude.HasValue)
                    && TryExtractCoordinatesFromText(row.Address, out var extractedLat, out var extractedLng))
                {
                    row.Latitude = extractedLat;
                    row.Longitude = extractedLng;
                    row.GeocodeStatus = "ok";
                    row.RetryAfterUtc = null;
                }

                if ((!row.Latitude.HasValue || !row.Longitude.HasValue) && hasGeocodeCache)
                {
                    var cached = await TryGetGeocodeCacheAsync(connection, row.BookingId, row.TenantId);
                    if (cached.Latitude.HasValue && cached.Longitude.HasValue)
                    {
                        row.Latitude = cached.Latitude;
                        row.Longitude = cached.Longitude;
                    }

                    if (!string.IsNullOrWhiteSpace(cached.Status))
                    {
                        row.GeocodeStatus = cached.Status;
                        row.RetryAfterUtc = cached.RetryAfterUtc;
                    }
                }

                rows.Add(row);
            }
        }

        rows = rows
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToList();

        var geocodeAttempts = 0;
        const int maxGeocodeAttemptsPerRequest = 25;

        foreach (var row in rows)
        {
            if (row.Latitude.HasValue && row.Longitude.HasValue)
            {
                continue;
            }

            if (TryExtractCoordinatesFromText(row.Address, out var parsedLat, out var parsedLng))
            {
                row.Latitude = parsedLat;
                row.Longitude = parsedLng;
                row.GeocodeStatus = "ok";
                row.RetryAfterUtc = null;

                if (hasGeocodeCache)
                {
                    await UpsertGeocodeCacheAsync(connection, row, null);
                }

                if (hasJobs && TryParseJobBookingId(row.BookingId, out var parsedJobId))
                {
                    await UpdateJobCoordinatesAsync(connection, row.TenantId, parsedJobId, parsedLat, parsedLng);
                }

                continue;
            }

            var isRecentBooking = nowUtc - row.CreatedAtUtc <= TimeSpan.FromHours(24);
            if (string.Equals(row.GeocodeStatus, "failed", StringComparison.OrdinalIgnoreCase) &&
                row.RetryAfterUtc.HasValue &&
                row.RetryAfterUtc.Value > nowUtc &&
                !isRecentBooking)
            {
                continue;
            }

            if (geocodeAttempts >= maxGeocodeAttemptsPerRequest)
            {
                break;
            }

            geocodeAttempts++;
            var geocodeResult = await TryGeocodeAddressAsync(row.Address);
            if (geocodeResult.Success && geocodeResult.Latitude.HasValue && geocodeResult.Longitude.HasValue)
            {
                row.Latitude = geocodeResult.Latitude;
                row.Longitude = geocodeResult.Longitude;
                row.GeocodeStatus = "ok";
                row.RetryAfterUtc = null;

                if (hasJobs && TryParseJobBookingId(row.BookingId, out var geocodedJobId))
                {
                    await UpdateJobCoordinatesAsync(
                        connection,
                        row.TenantId,
                        geocodedJobId,
                        geocodeResult.Latitude.Value,
                        geocodeResult.Longitude.Value);
                }
            }
            else
            {
                row.GeocodeStatus = "failed";
                row.RetryAfterUtc = nowUtc.AddMinutes(isRecentBooking ? 3 : 20);
            }

            if (hasGeocodeCache)
            {
                await UpsertGeocodeCacheAsync(connection, row, geocodeResult.ErrorMessage);
            }
        }

        var output = rows
            .Where(row => row.Latitude.HasValue && row.Longitude.HasValue)
            .Select(row => new DashboardMapPinItem
            {
                BookingId = row.BookingId,
                TenantId = row.TenantId,
                ServiceCategory = row.ServiceCategory,
                ServiceTitle = row.ServiceTitle,
                CustomerPhone = row.CustomerPhone,
                Address = row.Address,
                StartLocal = row.StartLocal,
                CreatedAtUtc = row.CreatedAtUtc,
                Latitude = row.Latitude ?? 0d,
                Longitude = row.Longitude ?? 0d
            })
            .ToList();

        return output;
    }

    public async Task<IReadOnlyList<ConversationThreadSummary>> GetConversationThreadsAsync(string tenantId, int limit)
    {
        var output = new List<ConversationThreadSummary>();
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 500);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasLegacyMessages = await TableExistsAsync(connection, "tg_conversation_messages");
        var hasLegacyState = await TableExistsAsync(connection, "tg_conversation_state");
        var hasMessagesLog = await TableExistsAsync(connection, "tg_MessagesLog");
        var hasUsers = await TableExistsAsync(connection, "tg_Users");
        var hasUserSessions = await TableExistsAsync(connection, "tg_UserSessions");
        var hasHandoffSessions = await TableExistsAsync(connection, "tg_human_handoff_sessions");

        if (hasLegacyMessages)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = hasLegacyState
                ? """
                  SELECT
                      t.phone,
                      t.last_message_at_utc,
                      COALESCE(last_msg.content, '') AS last_content,
                      COALESCE(json_extract(cs.slots_json, '$.menuContext'), '') AS menu_context,
                      COALESCE(last_msg.direction, '') AS last_direction
                  FROM (
                      SELECT phone, MAX(created_at_utc) AS last_message_at_utc
                      FROM tg_conversation_messages
                      WHERE tenant_id = @tenant_id
                      GROUP BY phone
                      ORDER BY last_message_at_utc DESC
                      LIMIT @limit
                  ) t
                  LEFT JOIN tg_conversation_messages last_msg
                      ON last_msg.tenant_id = @tenant_id
                     AND last_msg.phone = t.phone
                     AND last_msg.created_at_utc = t.last_message_at_utc
                  LEFT JOIN tg_conversation_state cs
                      ON cs.tenant_id = @tenant_id
                     AND cs.phone = t.phone
                  ORDER BY t.last_message_at_utc DESC;
                  """
                : """
                  SELECT
                      t.phone,
                      t.last_message_at_utc,
                      COALESCE(last_msg.content, '') AS last_content,
                      '',
                      COALESCE(last_msg.direction, '') AS last_direction
                  FROM (
                      SELECT phone, MAX(created_at_utc) AS last_message_at_utc
                      FROM tg_conversation_messages
                      WHERE tenant_id = @tenant_id
                      GROUP BY phone
                      ORDER BY last_message_at_utc DESC
                      LIMIT @limit
                  ) t
                  LEFT JOIN tg_conversation_messages last_msg
                      ON last_msg.tenant_id = @tenant_id
                     AND last_msg.phone = t.phone
                     AND last_msg.created_at_utc = t.last_message_at_utc
                  ORDER BY t.last_message_at_utc DESC;
                  """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var menuContext = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                output.Add(new ConversationThreadSummary
                {
                    Phone = reader.GetString(0),
                    LastMessageAtUtc = ParseUtc(reader.GetString(1)),
                    LastMessagePreview = TrimPreview(reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                    MenuContext = menuContext,
                    IsInHumanHandoff = string.Equals(menuContext, "human_handoff", StringComparison.OrdinalIgnoreCase),
                    LastMessageDirection = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                });
            }
        }

        if (hasMessagesLog)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = hasUsers && hasUserSessions
                ? """
                  SELECT
                      m.TelegramUserId,
                      m.CreatedAt,
                      COALESCE(m.Text, '') AS last_text,
                      COALESCE(s.State, '') AS menu_context,
                      COALESCE(m.Direction, '') AS last_direction
                  FROM tg_MessagesLog m
                  INNER JOIN (
                      SELECT TelegramUserId, MAX(Id) AS LastId
                      FROM tg_MessagesLog
                      WHERE TenantId = @tenant_id
                      GROUP BY TelegramUserId
                  ) t ON t.LastId = m.Id
                  LEFT JOIN tg_Users u
                      ON u.TenantId = @tenant_id
                     AND u.TelegramUserId = m.TelegramUserId
                  LEFT JOIN tg_UserSessions s
                      ON s.UserId = u.Id
                  WHERE m.TenantId = @tenant_id
                  ORDER BY m.CreatedAt DESC
                  LIMIT @limit;
                  """
                : """
                  SELECT
                      m.TelegramUserId,
                      m.CreatedAt,
                      COALESCE(m.Text, '') AS last_text,
                      '',
                      COALESCE(m.Direction, '') AS last_direction
                  FROM tg_MessagesLog m
                  INNER JOIN (
                      SELECT TelegramUserId, MAX(Id) AS LastId
                      FROM tg_MessagesLog
                      WHERE TenantId = @tenant_id
                      GROUP BY TelegramUserId
                  ) t ON t.LastId = m.Id
                  WHERE m.TenantId = @tenant_id
                  ORDER BY m.CreatedAt DESC
                  LIMIT @limit;
                  """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var telegramUserId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                if (telegramUserId <= 0)
                {
                    continue;
                }

                var menuContext = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                output.Add(new ConversationThreadSummary
                {
                    Phone = $"tg:{telegramUserId}",
                    LastMessageAtUtc = ParseUtc(reader.IsDBNull(1) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(1)),
                    LastMessagePreview = TrimPreview(reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                    MenuContext = menuContext,
                    IsInHumanHandoff = string.Equals(menuContext, "human_handoff", StringComparison.OrdinalIgnoreCase),
                    LastMessageDirection = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                });
            }
        }

        output = output
            .GroupBy(x => x.Phone, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(x => x.LastMessageAtUtc)
                .First())
            .OrderByDescending(x => x.LastMessageAtUtc)
            .Take(safeLimit)
            .ToList();

        if (hasHandoffSessions && output.Count > 0)
        {
            var openHandoffUsers = new Dictionary<long, bool>();
            await using var handoffCommand = connection.CreateCommand();
            handoffCommand.CommandText =
                """
                SELECT telegram_user_id, assigned_agent, accepted_at_utc
                FROM tg_human_handoff_sessions
                WHERE tenant_id = @tenant_id
                  AND is_open = 1;
                """;
            handoffCommand.Parameters.AddWithValue("@tenant_id", tenant);

            await using var handoffReader = await handoffCommand.ExecuteReaderAsync();
            while (await handoffReader.ReadAsync())
            {
                if (!handoffReader.IsDBNull(0))
                {
                    var telegramUserId = handoffReader.GetInt64(0);
                    var assignedAgent = handoffReader.IsDBNull(1) ? string.Empty : handoffReader.GetString(1);
                    DateTimeOffset? acceptedAtUtc = handoffReader.IsDBNull(2)
                        ? null
                        : ParseUtc(handoffReader.GetString(2));
                    var awaitingHumanPickup = string.IsNullOrWhiteSpace(assignedAgent) || !acceptedAtUtc.HasValue;

                    openHandoffUsers[telegramUserId] = awaitingHumanPickup;
                }
            }

            if (openHandoffUsers.Count > 0)
            {
                foreach (var item in output)
                {
                    if (!TryParseTelegramUserId(item.Phone, out var telegramUserId))
                    {
                        continue;
                    }

                    if (openHandoffUsers.TryGetValue(telegramUserId, out var awaitingHumanPickup))
                    {
                        item.IsInHumanHandoff = true;
                        item.MenuContext = "human_handoff";
                        item.IsAwaitingHumanReply = awaitingHumanPickup
                            || IsInboundConversationDirection(item.LastMessageDirection);
                    }
                }
            }
        }

        return output;
    }

    public async Task<IReadOnlyList<ConversationMessageItem>> GetConversationMessagesAsync(string tenantId, string phone, int limit)
    {
        var output = new List<ConversationMessageItem>();
        var tenant = NormalizeTenant(tenantId);
        var normalizedPhone = phone.Trim();
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var isTelegramThread = normalizedPhone.StartsWith("tg:", StringComparison.OrdinalIgnoreCase);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasMessagesLog = await TableExistsAsync(connection, "tg_MessagesLog");
        if (isTelegramThread && hasMessagesLog && TryParseTelegramUserId(normalizedPhone, out var telegramUserId))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id, Direction, MessageType, Text, CreatedAt
                FROM tg_MessagesLog
                WHERE TenantId = @tenant_id
                  AND TelegramUserId = @telegram_user_id
                ORDER BY CreatedAt DESC, Id DESC
                LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@telegram_user_id", telegramUserId);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var direction = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var messageType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var role = ResolveTelegramRole(direction, messageType);

                output.Add(new ConversationMessageItem
                {
                    Id = reader.GetInt64(0),
                    Direction = direction,
                    Role = role,
                    Content = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ToolName = null,
                    ToolCallId = null,
                    CreatedAtUtc = ParseUtc(reader.IsDBNull(4) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(4))
                });
            }
        }

        var hasLegacyMessages = await TableExistsAsync(connection, "tg_conversation_messages");
        if (output.Count == 0 && hasLegacyMessages)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, direction, role, content, tool_name, tool_call_id, created_at_utc
                FROM tg_conversation_messages
                WHERE tenant_id = @tenant_id
                  AND phone = @phone
                ORDER BY created_at_utc DESC, id DESC
                LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@phone", normalizedPhone);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                output.Add(new ConversationMessageItem
                {
                    Id = reader.GetInt64(0),
                    Direction = reader.GetString(1),
                    Role = reader.GetString(2),
                    Content = reader.GetString(3),
                    ToolName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ToolCallId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedAtUtc = ParseUtc(reader.GetString(6))
                });
            }
        }

        output.Reverse();
        return output;
    }

    public async Task<ConversationHandoffStatus> GetConversationHandoffStatusAsync(string tenantId, string phone)
    {
        var tenant = NormalizeTenant(tenantId);
        var normalizedPhone = phone?.Trim() ?? string.Empty;
        var output = BuildUnavailableHandoffStatus(tenant, normalizedPhone);

        if (!TryParseTelegramUserId(normalizedPhone, out var telegramUserId))
        {
            return output;
        }

        output.IsTelegramThread = true;

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        if (!await TableExistsAsync(connection, "tg_human_handoff_sessions"))
        {
            return output;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                requested_by_role,
                is_open,
                requested_at_utc,
                accepted_at_utc,
                closed_at_utc,
                assigned_agent,
                previous_state,
                close_reason,
                last_message_at_utc
            FROM tg_human_handoff_sessions
            WHERE tenant_id = @tenant_id
              AND telegram_user_id = @telegram_user_id
            ORDER BY requested_at_utc DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@telegram_user_id", telegramUserId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return output;
        }

        output.RequestedByRole = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        output.IsOpen = !reader.IsDBNull(1) && reader.GetInt32(1) == 1;
        output.RequestedAtUtc = reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2));
        output.AcceptedAtUtc = reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3));
        output.ClosedAtUtc = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4));
        output.AssignedAgent = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        output.PreviousState = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
        output.CloseReason = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
        output.LastMessageAtUtc = reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8));

        return output;
    }

    public async Task<ConversationHandoffStatus> OpenConversationHandoffAsync(string tenantId, string phone, string? agent)
    {
        var tenant = NormalizeTenant(tenantId);
        var normalizedPhone = phone?.Trim() ?? string.Empty;
        var output = BuildUnavailableHandoffStatus(tenant, normalizedPhone);
        var safeAgent = string.IsNullOrWhiteSpace(agent) ? "admin" : agent.Trim();

        if (!TryParseTelegramUserId(normalizedPhone, out var telegramUserId))
        {
            return output;
        }

        output.IsTelegramThread = true;

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        if (!await TableExistsAsync(connection, "tg_human_handoff_sessions"))
        {
            return output;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var nowText = ToUtcText(nowUtc);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        var hasUsers = await TableExistsAsync(connection, "tg_Users", transaction);
        var hasUserSessions = await TableExistsAsync(connection, "tg_UserSessions", transaction);

        long? appUserId = null;
        string previousState = string.Empty;
        string role = string.Empty;

        if (hasUsers)
        {
            await using var userCommand = connection.CreateCommand();
            userCommand.Transaction = transaction;
            userCommand.CommandText = hasUserSessions
                ? """
                  SELECT u.Id, COALESCE(s.State, ''), COALESCE(u.Role, '')
                  FROM tg_Users u
                  LEFT JOIN tg_UserSessions s ON s.UserId = u.Id
                  WHERE u.TenantId = @tenant_id
                    AND u.TelegramUserId = @telegram_user_id
                  LIMIT 1;
                  """
                : """
                  SELECT u.Id, '', COALESCE(u.Role, '')
                  FROM tg_Users u
                  WHERE u.TenantId = @tenant_id
                    AND u.TelegramUserId = @telegram_user_id
                  LIMIT 1;
                  """;
            userCommand.Parameters.AddWithValue("@tenant_id", tenant);
            userCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);

            await using var userReader = await userCommand.ExecuteReaderAsync();
            if (await userReader.ReadAsync())
            {
                appUserId = userReader.IsDBNull(0) ? null : userReader.GetInt64(0);
                previousState = userReader.IsDBNull(1) ? string.Empty : userReader.GetString(1).Trim();
                role = userReader.IsDBNull(2) ? string.Empty : userReader.GetString(2).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(previousState) || string.Equals(previousState, "human_handoff", StringComparison.OrdinalIgnoreCase))
        {
            previousState = ResolveHomeStateFromRole(role);
        }

        long openSessionId = 0;
        await using (var openCommand = connection.CreateCommand())
        {
            openCommand.Transaction = transaction;
            openCommand.CommandText =
                """
                SELECT id
                FROM tg_human_handoff_sessions
                WHERE tenant_id = @tenant_id
                  AND telegram_user_id = @telegram_user_id
                  AND is_open = 1
                ORDER BY requested_at_utc DESC, id DESC
                LIMIT 1;
                """;
            openCommand.Parameters.AddWithValue("@tenant_id", tenant);
            openCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);

            await using var openReader = await openCommand.ExecuteReaderAsync();
            if (await openReader.ReadAsync())
            {
                openSessionId = openReader.IsDBNull(0) ? 0L : openReader.GetInt64(0);
            }
        }

        if (openSessionId > 0)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                UPDATE tg_human_handoff_sessions
                SET accepted_at_utc = COALESCE(accepted_at_utc, @accepted_at_utc),
                    assigned_agent = CASE
                        WHEN @assigned_agent IS NULL OR TRIM(@assigned_agent) = '' THEN assigned_agent
                        ELSE @assigned_agent
                    END,
                    previous_state = CASE
                        WHEN (previous_state IS NULL OR TRIM(previous_state) = '') AND @previous_state IS NOT NULL AND TRIM(@previous_state) <> '' THEN @previous_state
                        ELSE previous_state
                    END,
                    last_message_at_utc = @last_message_at_utc
                WHERE id = @id;
                """;
            updateCommand.Parameters.AddWithValue("@accepted_at_utc", nowText);
            updateCommand.Parameters.AddWithValue("@assigned_agent", safeAgent);
            updateCommand.Parameters.AddWithValue("@previous_state", previousState);
            updateCommand.Parameters.AddWithValue("@last_message_at_utc", nowText);
            updateCommand.Parameters.AddWithValue("@id", openSessionId);
            await updateCommand.ExecuteNonQueryAsync();
        }
        else
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                INSERT INTO tg_human_handoff_sessions
                (
                    tenant_id,
                    telegram_user_id,
                    app_user_id,
                    requested_by_role,
                    is_open,
                    requested_at_utc,
                    accepted_at_utc,
                    closed_at_utc,
                    assigned_agent,
                    previous_state,
                    close_reason,
                    last_message_at_utc
                )
                VALUES
                (
                    @tenant_id,
                    @telegram_user_id,
                    @app_user_id,
                    @requested_by_role,
                    1,
                    @requested_at_utc,
                    @accepted_at_utc,
                    NULL,
                    @assigned_agent,
                    @previous_state,
                    NULL,
                    @last_message_at_utc
                );
                """;
            insertCommand.Parameters.AddWithValue("@tenant_id", tenant);
            insertCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);
            insertCommand.Parameters.AddWithValue("@app_user_id", appUserId.HasValue ? appUserId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue("@requested_by_role", "admin");
            insertCommand.Parameters.AddWithValue("@requested_at_utc", nowText);
            insertCommand.Parameters.AddWithValue("@accepted_at_utc", nowText);
            insertCommand.Parameters.AddWithValue("@assigned_agent", safeAgent);
            insertCommand.Parameters.AddWithValue("@previous_state", previousState);
            insertCommand.Parameters.AddWithValue("@last_message_at_utc", nowText);
            await insertCommand.ExecuteNonQueryAsync();
        }

        if (hasUserSessions && appUserId.HasValue)
        {
            await using var updateSessionCommand = connection.CreateCommand();
            updateSessionCommand.Transaction = transaction;
            updateSessionCommand.CommandText =
                """
                UPDATE tg_UserSessions
                SET State = 'human_handoff',
                    IsChatActive = 0,
                    ChatJobId = NULL,
                    ChatPeerUserId = NULL,
                    UpdatedAt = @updated_at
                WHERE UserId = @user_id;
                """;
            updateSessionCommand.Parameters.AddWithValue("@updated_at", nowText);
            updateSessionCommand.Parameters.AddWithValue("@user_id", appUserId.Value);
            var affected = await updateSessionCommand.ExecuteNonQueryAsync();

            if (affected == 0)
            {
                await using var insertSessionCommand = connection.CreateCommand();
                insertSessionCommand.Transaction = transaction;
                insertSessionCommand.CommandText =
                    """
                    INSERT INTO tg_UserSessions (UserId, State, DraftJson, ActiveJobId, ChatJobId, ChatPeerUserId, IsChatActive, UpdatedAt)
                    VALUES (@user_id, 'human_handoff', '{}', NULL, NULL, NULL, 0, @updated_at);
                    """;
                insertSessionCommand.Parameters.AddWithValue("@user_id", appUserId.Value);
                insertSessionCommand.Parameters.AddWithValue("@updated_at", nowText);
                await insertSessionCommand.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
        return await GetConversationHandoffStatusAsync(tenant, normalizedPhone);
    }

    public async Task<ConversationHandoffStatus> CloseConversationHandoffAsync(string tenantId, string phone, string? agent, string? reason)
    {
        var tenant = NormalizeTenant(tenantId);
        var normalizedPhone = phone?.Trim() ?? string.Empty;
        var output = BuildUnavailableHandoffStatus(tenant, normalizedPhone);
        var safeAgent = string.IsNullOrWhiteSpace(agent) ? "admin" : agent.Trim();
        var safeReason = string.IsNullOrWhiteSpace(reason) ? "Encerrado pelo admin" : reason.Trim();

        if (!TryParseTelegramUserId(normalizedPhone, out var telegramUserId))
        {
            return output;
        }

        output.IsTelegramThread = true;

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        if (!await TableExistsAsync(connection, "tg_human_handoff_sessions"))
        {
            return output;
        }

        var nowText = ToUtcText(DateTimeOffset.UtcNow);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        var hasUsers = await TableExistsAsync(connection, "tg_Users", transaction);
        var hasUserSessions = await TableExistsAsync(connection, "tg_UserSessions", transaction);

        long? appUserId = null;
        string role = string.Empty;
        if (hasUsers)
        {
            await using var userCommand = connection.CreateCommand();
            userCommand.Transaction = transaction;
            userCommand.CommandText =
                """
                SELECT Id, COALESCE(Role, '')
                FROM tg_Users
                WHERE TenantId = @tenant_id
                  AND TelegramUserId = @telegram_user_id
                LIMIT 1;
                """;
            userCommand.Parameters.AddWithValue("@tenant_id", tenant);
            userCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);

            await using var userReader = await userCommand.ExecuteReaderAsync();
            if (await userReader.ReadAsync())
            {
                appUserId = userReader.IsDBNull(0) ? null : userReader.GetInt64(0);
                role = userReader.IsDBNull(1) ? string.Empty : userReader.GetString(1);
            }
        }

        string previousState = string.Empty;
        long openSessionId = 0;
        await using (var openCommand = connection.CreateCommand())
        {
            openCommand.Transaction = transaction;
            openCommand.CommandText =
                """
                SELECT id, COALESCE(previous_state, '')
                FROM tg_human_handoff_sessions
                WHERE tenant_id = @tenant_id
                  AND telegram_user_id = @telegram_user_id
                  AND is_open = 1
                ORDER BY requested_at_utc DESC, id DESC
                LIMIT 1;
                """;
            openCommand.Parameters.AddWithValue("@tenant_id", tenant);
            openCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);

            await using var openReader = await openCommand.ExecuteReaderAsync();
            if (await openReader.ReadAsync())
            {
                openSessionId = openReader.IsDBNull(0) ? 0L : openReader.GetInt64(0);
                previousState = openReader.IsDBNull(1) ? string.Empty : openReader.GetString(1);
            }
        }

        if (openSessionId > 0)
        {
            await using var closeCommand = connection.CreateCommand();
            closeCommand.Transaction = transaction;
            closeCommand.CommandText =
                """
                UPDATE tg_human_handoff_sessions
                SET is_open = 0,
                    closed_at_utc = @closed_at_utc,
                    assigned_agent = CASE
                        WHEN @assigned_agent IS NULL OR TRIM(@assigned_agent) = '' THEN assigned_agent
                        ELSE @assigned_agent
                    END,
                    close_reason = @close_reason,
                    last_message_at_utc = @last_message_at_utc
                WHERE id = @id;
                """;
            closeCommand.Parameters.AddWithValue("@closed_at_utc", nowText);
            closeCommand.Parameters.AddWithValue("@assigned_agent", safeAgent);
            closeCommand.Parameters.AddWithValue("@close_reason", safeReason);
            closeCommand.Parameters.AddWithValue("@last_message_at_utc", nowText);
            closeCommand.Parameters.AddWithValue("@id", openSessionId);
            await closeCommand.ExecuteNonQueryAsync();
        }

        var resumeState = string.IsNullOrWhiteSpace(previousState) || string.Equals(previousState, "human_handoff", StringComparison.OrdinalIgnoreCase)
            ? ResolveHomeStateFromRole(role)
            : previousState.Trim();

        if (hasUserSessions && appUserId.HasValue)
        {
            await using var resumeCommand = connection.CreateCommand();
            resumeCommand.Transaction = transaction;
            resumeCommand.CommandText =
                """
                UPDATE tg_UserSessions
                SET State = @state,
                    IsChatActive = 0,
                    ChatJobId = NULL,
                    ChatPeerUserId = NULL,
                    UpdatedAt = @updated_at
                WHERE UserId = @user_id;
                """;
            resumeCommand.Parameters.AddWithValue("@state", resumeState);
            resumeCommand.Parameters.AddWithValue("@updated_at", nowText);
            resumeCommand.Parameters.AddWithValue("@user_id", appUserId.Value);
            await resumeCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return await GetConversationHandoffStatusAsync(tenant, normalizedPhone);
    }

    public async Task<SendHumanMessageResult> SendHumanMessageAsync(string tenantId, string phone, string message, string? agent)
    {
        var tenant = NormalizeTenant(tenantId);
        var normalizedPhone = phone?.Trim() ?? string.Empty;
        var safeMessage = message?.Trim() ?? string.Empty;
        var safeAgent = string.IsNullOrWhiteSpace(agent) ? "admin" : agent.Trim();

        if (!TryParseTelegramUserId(normalizedPhone, out var telegramUserId))
        {
            return new SendHumanMessageResult
            {
                Success = false,
                Error = "A conversa nao pertence ao canal Telegram.",
                Handoff = BuildUnavailableHandoffStatus(tenant, normalizedPhone)
            };
        }

        if (string.IsNullOrWhiteSpace(safeMessage))
        {
            return new SendHumanMessageResult
            {
                Success = false,
                Error = "Informe uma mensagem para envio.",
                Handoff = BuildUnavailableHandoffStatus(tenant, normalizedPhone)
            };
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasTelegramConfig = await TableExistsAsync(connection, "tg_tenant_telegram_config");
        if (!hasTelegramConfig)
        {
            return new SendHumanMessageResult
            {
                Success = false,
                Error = "Configuracao Telegram nao encontrada para este tenant.",
                Handoff = BuildUnavailableHandoffStatus(tenant, normalizedPhone)
            };
        }

        string token;
        await using (var tokenCommand = connection.CreateCommand())
        {
            tokenCommand.CommandText =
                """
                SELECT bot_token
                FROM tg_tenant_telegram_config
                WHERE tenant_id = @tenant_id
                LIMIT 1;
                """;
            tokenCommand.Parameters.AddWithValue("@tenant_id", tenant);
            token = Convert.ToString(await tokenCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new SendHumanMessageResult
            {
                Success = false,
                Error = "Token do bot Telegram nao configurado para este tenant.",
                Handoff = BuildUnavailableHandoffStatus(tenant, normalizedPhone)
            };
        }

        var opened = await OpenConversationHandoffAsync(tenant, normalizedPhone, safeAgent);
        var telegramResult = await SendTelegramTextAsync(token, telegramUserId, safeMessage);
        if (!telegramResult.Success)
        {
            return new SendHumanMessageResult
            {
                Success = false,
                Error = telegramResult.Error,
                Handoff = opened
            };
        }

        var nowText = ToUtcText(DateTimeOffset.UtcNow);
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            var hasMessagesLog = await TableExistsAsync(connection, "tg_MessagesLog", transaction);
            if (hasMessagesLog)
            {
                await using var messageCommand = connection.CreateCommand();
                messageCommand.Transaction = transaction;
                messageCommand.CommandText =
                    """
                    INSERT INTO tg_MessagesLog
                    (
                        TenantId,
                        TelegramUserId,
                        Direction,
                        MessageType,
                        Text,
                        TelegramMessageId,
                        RelatedJobId,
                        CreatedAt
                    )
                    VALUES
                    (
                        @tenant_id,
                        @telegram_user_id,
                        'Out',
                        'Event',
                        @text,
                        @telegram_message_id,
                        NULL,
                        @created_at
                    );
                    """;
                messageCommand.Parameters.AddWithValue("@tenant_id", tenant);
                messageCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);
                messageCommand.Parameters.AddWithValue("@text", safeMessage);
                messageCommand.Parameters.AddWithValue("@telegram_message_id", telegramResult.MessageId.HasValue ? telegramResult.MessageId.Value : DBNull.Value);
                messageCommand.Parameters.AddWithValue("@created_at", nowText);
                await messageCommand.ExecuteNonQueryAsync();
            }

            if (await TableExistsAsync(connection, "tg_human_handoff_sessions", transaction))
            {
                await using var handoffCommand = connection.CreateCommand();
                handoffCommand.Transaction = transaction;
                handoffCommand.CommandText =
                    """
                    UPDATE tg_human_handoff_sessions
                    SET accepted_at_utc = COALESCE(accepted_at_utc, @accepted_at_utc),
                        assigned_agent = CASE
                            WHEN @assigned_agent IS NULL OR TRIM(@assigned_agent) = '' THEN assigned_agent
                            ELSE @assigned_agent
                        END,
                        last_message_at_utc = @last_message_at_utc
                    WHERE tenant_id = @tenant_id
                      AND telegram_user_id = @telegram_user_id
                      AND is_open = 1;
                    """;
                handoffCommand.Parameters.AddWithValue("@accepted_at_utc", nowText);
                handoffCommand.Parameters.AddWithValue("@assigned_agent", safeAgent);
                handoffCommand.Parameters.AddWithValue("@last_message_at_utc", nowText);
                handoffCommand.Parameters.AddWithValue("@tenant_id", tenant);
                handoffCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);
                await handoffCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        return new SendHumanMessageResult
        {
            Success = true,
            Error = string.Empty,
            TelegramMessageId = telegramResult.MessageId,
            Handoff = await GetConversationHandoffStatusAsync(tenant, normalizedPhone)
        };
    }

    public async Task<ConversationOrderClientContext> GetConversationOrderClientContextAsync(string tenantId, string phone)
    {
        var tenant = NormalizeTenant(tenantId);
        var normalizedPhone = phone?.Trim() ?? string.Empty;
        var output = new ConversationOrderClientContext
        {
            TenantId = tenant,
            Phone = normalizedPhone
        };

        if (!TryParseTelegramUserId(normalizedPhone, out var telegramUserId))
        {
            return output;
        }

        output.IsTelegramThread = true;

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        if (!await TableExistsAsync(connection, "tg_Users"))
        {
            return output;
        }

        var hasClientProfiles = await TableExistsAsync(connection, "tg_client_profiles");

        await using var command = connection.CreateCommand();
        command.CommandText = hasClientProfiles
            ? """
              SELECT
                COALESCE(u.Role, ''),
                COALESCE(cp.is_registration_complete, 0),
                COALESCE(cp.full_name, u.Name, ''),
                COALESCE(cp.phone_number, u.Phone, ''),
                COALESCE(cp.cep, ''),
                COALESCE(cp.street, ''),
                COALESCE(cp.number, ''),
                COALESCE(cp.complement, ''),
                COALESCE(cp.neighborhood, ''),
                COALESCE(cp.city, ''),
                COALESCE(cp.state, ''),
                cp.latitude,
                cp.longitude
              FROM tg_Users u
              LEFT JOIN tg_client_profiles cp ON cp.user_id = u.Id
              WHERE u.TenantId = @tenant_id
                AND u.TelegramUserId = @telegram_user_id
              LIMIT 1;
              """
            : """
              SELECT
                COALESCE(u.Role, ''),
                0,
                COALESCE(u.Name, ''),
                COALESCE(u.Phone, ''),
                '',
                '',
                '',
                '',
                '',
                '',
                '',
                NULL,
                NULL
              FROM tg_Users u
              WHERE u.TenantId = @tenant_id
                AND u.TelegramUserId = @telegram_user_id
              LIMIT 1;
              """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@telegram_user_id", telegramUserId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return output;
        }

        output.UserExists = true;
        output.UserRole = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        output.IsRegistrationComplete = !reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) != 0;
        output.ClientName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        output.ContactPhone = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        output.Cep = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
        output.Street = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        output.Number = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
        output.Complement = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
        output.Neighborhood = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
        output.City = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);
        output.State = reader.IsDBNull(10) ? string.Empty : reader.GetString(10);
        output.Latitude = reader.IsDBNull(11) ? null : reader.GetDouble(11);
        output.Longitude = reader.IsDBNull(12) ? null : reader.GetDouble(12);
        output.IsClientEligible = !string.Equals(output.UserRole?.Trim(), "Provider", StringComparison.OrdinalIgnoreCase);

        return output;
    }

    public async Task<ConversationOrderDraftResult> SendClientOrderConfirmationAsync(ConversationOrderDraftCommand input, string? agent)
    {
        var tenant = NormalizeTenant(input.TenantId);
        var normalizedPhone = input.Phone?.Trim() ?? string.Empty;
        var safeAgent = string.IsNullOrWhiteSpace(agent) ? "admin" : agent.Trim();
        var output = new ConversationOrderDraftResult
        {
            Success = false,
            Error = string.Empty,
            Handoff = BuildUnavailableHandoffStatus(tenant, normalizedPhone)
        };

        if (!TryParseTelegramUserId(normalizedPhone, out var telegramUserId))
        {
            output.Error = "A conversa nao pertence ao canal Telegram.";
            return output;
        }

        if (string.IsNullOrWhiteSpace(input.Category)
            || string.IsNullOrWhiteSpace(input.Description)
            || string.IsNullOrWhiteSpace(input.AddressText)
            || string.IsNullOrWhiteSpace(input.PreferenceCode)
            || string.IsNullOrWhiteSpace(input.ContactName)
            || string.IsNullOrWhiteSpace(input.ContactPhone))
        {
            output.Error = "Preencha todos os dados obrigatorios do pedido.";
            return output;
        }

        if (!input.IsUrgent && !input.ScheduledAt.HasValue)
        {
            output.Error = "Informe a data e hora do atendimento.";
            return output;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasUsers = await TableExistsAsync(connection, "tg_Users");
        var hasUserSessions = await TableExistsAsync(connection, "tg_UserSessions");
        var hasTelegramConfig = await TableExistsAsync(connection, "tg_tenant_telegram_config");
        var hasClientProfiles = await TableExistsAsync(connection, "tg_client_profiles");
        var hasMessagesLog = await TableExistsAsync(connection, "tg_MessagesLog");
        var hasHandoffSessions = await TableExistsAsync(connection, "tg_human_handoff_sessions");

        if (!hasUsers || !hasUserSessions)
        {
            output.Error = "A estrutura do bot nao esta pronta para receber o pedido assistido.";
            return output;
        }

        if (!hasTelegramConfig)
        {
            output.Error = "Configuracao Telegram nao encontrada para este tenant.";
            return output;
        }

        long? appUserId = null;
        string role = string.Empty;
        var registrationComplete = false;
        await using (var userCommand = connection.CreateCommand())
        {
            userCommand.CommandText = hasClientProfiles
                ? """
                  SELECT
                    u.Id,
                    COALESCE(u.Role, ''),
                    COALESCE(cp.is_registration_complete, 0)
                  FROM tg_Users u
                  LEFT JOIN tg_client_profiles cp ON cp.user_id = u.Id
                  WHERE u.TenantId = @tenant_id
                    AND u.TelegramUserId = @telegram_user_id
                  LIMIT 1;
                  """
                : """
                  SELECT
                    u.Id,
                    COALESCE(u.Role, ''),
                    0
                  FROM tg_Users u
                  WHERE u.TenantId = @tenant_id
                    AND u.TelegramUserId = @telegram_user_id
                  LIMIT 1;
                  """;
            userCommand.Parameters.AddWithValue("@tenant_id", tenant);
            userCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);

            await using var reader = await userCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                appUserId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                role = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                registrationComplete = !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0;
            }
        }

        if (!appUserId.HasValue)
        {
            output.Error = "Cliente nao encontrado neste tenant.";
            return output;
        }

        if (string.Equals(role?.Trim(), "Provider", StringComparison.OrdinalIgnoreCase))
        {
            output.Error = "Esta conversa pertence a um prestador. O pedido assistido so pode ser aberto para clientes.";
            return output;
        }

        if (!registrationComplete)
        {
            output.Error = "O cliente ainda nao concluiu o cadastro. Conclua o cadastro antes de abrir um pedido assistido.";
            return output;
        }

        string token;
        await using (var tokenCommand = connection.CreateCommand())
        {
            tokenCommand.CommandText =
                """
                SELECT bot_token
                FROM tg_tenant_telegram_config
                WHERE tenant_id = @tenant_id
                LIMIT 1;
                """;
            tokenCommand.Parameters.AddWithValue("@tenant_id", tenant);
            token = Convert.ToString(await tokenCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            output.Error = "Token do bot Telegram nao configurado para este tenant.";
            return output;
        }

        var normalizedCommand = new ConversationOrderDraftCommand
        {
            TenantId = tenant,
            Phone = normalizedPhone,
            Category = input.Category.Trim(),
            Description = input.Description.Trim(),
            AddressText = input.AddressText.Trim(),
            Cep = NormalizeCepDigits(input.Cep),
            Latitude = input.Latitude,
            Longitude = input.Longitude,
            IsUrgent = input.IsUrgent,
            ScheduledAt = input.IsUrgent ? DateTimeOffset.UtcNow : input.ScheduledAt,
            PreferenceCode = input.PreferenceCode.Trim().ToUpperInvariant(),
            ContactName = input.ContactName.Trim(),
            ContactPhone = input.ContactPhone.Trim()
        };

        var nowUtc = DateTimeOffset.UtcNow;
        var nowText = ToUtcText(nowUtc);
        var draftJson = BuildAssistedOrderDraftJson(normalizedCommand);
        var confirmationText = BuildAssistedOrderConfirmationText(normalizedCommand);
        var confirmationKeyboard = BuildClientOrderConfirmationKeyboard();

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using (var sessionCommand = connection.CreateCommand())
            {
                sessionCommand.Transaction = transaction;
                sessionCommand.CommandText =
                    """
                    UPDATE tg_UserSessions
                    SET State = 'C_CONFIRM',
                        DraftJson = @draft_json,
                        ActiveJobId = NULL,
                        ChatJobId = NULL,
                        ChatPeerUserId = NULL,
                        IsChatActive = 0,
                        UpdatedAt = @updated_at
                    WHERE UserId = @user_id;
                    """;
                sessionCommand.Parameters.AddWithValue("@draft_json", draftJson);
                sessionCommand.Parameters.AddWithValue("@updated_at", nowText);
                sessionCommand.Parameters.AddWithValue("@user_id", appUserId.Value);
                var affected = await sessionCommand.ExecuteNonQueryAsync();

                if (affected == 0)
                {
                    await using var insertSessionCommand = connection.CreateCommand();
                    insertSessionCommand.Transaction = transaction;
                    insertSessionCommand.CommandText =
                        """
                        INSERT INTO tg_UserSessions
                        (UserId, State, DraftJson, ActiveJobId, ChatJobId, ChatPeerUserId, IsChatActive, UpdatedAt)
                        VALUES
                        (@user_id, 'C_CONFIRM', @draft_json, NULL, NULL, NULL, 0, @updated_at);
                        """;
                    insertSessionCommand.Parameters.AddWithValue("@user_id", appUserId.Value);
                    insertSessionCommand.Parameters.AddWithValue("@draft_json", draftJson);
                    insertSessionCommand.Parameters.AddWithValue("@updated_at", nowText);
                    await insertSessionCommand.ExecuteNonQueryAsync();
                }
            }

            var telegramResult = await SendTelegramTextAsync(token, telegramUserId, confirmationText, confirmationKeyboard);
            if (!telegramResult.Success)
            {
                await transaction.RollbackAsync();
                output.Error = telegramResult.Error;
                return output;
            }

            if (hasMessagesLog)
            {
                await using var messageCommand = connection.CreateCommand();
                messageCommand.Transaction = transaction;
                messageCommand.CommandText =
                    """
                    INSERT INTO tg_MessagesLog
                    (
                        TenantId,
                        TelegramUserId,
                        Direction,
                        MessageType,
                        Text,
                        TelegramMessageId,
                        RelatedJobId,
                        CreatedAt
                    )
                    VALUES
                    (
                        @tenant_id,
                        @telegram_user_id,
                        'Out',
                        'Text',
                        @text,
                        @telegram_message_id,
                        NULL,
                        @created_at
                    );
                    """;
                messageCommand.Parameters.AddWithValue("@tenant_id", tenant);
                messageCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);
                messageCommand.Parameters.AddWithValue("@text", confirmationText);
                messageCommand.Parameters.AddWithValue("@telegram_message_id", telegramResult.MessageId.HasValue ? telegramResult.MessageId.Value : DBNull.Value);
                messageCommand.Parameters.AddWithValue("@created_at", nowText);
                await messageCommand.ExecuteNonQueryAsync();
            }

            if (hasHandoffSessions)
            {
                await using var handoffCommand = connection.CreateCommand();
                handoffCommand.Transaction = transaction;
                handoffCommand.CommandText =
                    """
                    UPDATE tg_human_handoff_sessions
                    SET is_open = 0,
                        closed_at_utc = @closed_at_utc,
                        close_reason = @close_reason,
                        last_message_at_utc = @last_message_at_utc
                    WHERE tenant_id = @tenant_id
                      AND telegram_user_id = @telegram_user_id
                      AND is_open = 1;
                    """;
                handoffCommand.Parameters.AddWithValue("@closed_at_utc", nowText);
                handoffCommand.Parameters.AddWithValue("@close_reason", "pedido_enviado_para_confirmacao");
                handoffCommand.Parameters.AddWithValue("@last_message_at_utc", nowText);
                handoffCommand.Parameters.AddWithValue("@tenant_id", tenant);
                handoffCommand.Parameters.AddWithValue("@telegram_user_id", telegramUserId);
                await handoffCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();

            output.Success = true;
            output.Error = string.Empty;
            output.TelegramMessageId = telegramResult.MessageId;
            output.Handoff = await GetConversationHandoffStatusAsync(tenant, normalizedPhone);
            return output;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<BookingListItem>> GetBookingsAsync(string tenantId, int limit)
    {
        var output = new List<BookingListItem>();
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 1000);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasLegacyBookings = await TableExistsAsync(connection, "tg_bookings");
        var hasGeocodeCache = await TableExistsAsync(connection, "tg_booking_geocode_cache");
        var hasJobs = await TableExistsAsync(connection, "tg_Jobs");
        var hasClientProfiles = await TableExistsAsync(connection, "tg_client_profiles");
        var hasUsers = await TableExistsAsync(connection, "tg_Users");
        var hasGoogleCalendarConfig = await TableExistsAsync(connection, "tg_tenant_google_calendar_config");
        var defaultJobDurationMinutes = 60;

        if (hasGoogleCalendarConfig)
        {
            await using var durationCommand = connection.CreateCommand();
            durationCommand.CommandText =
                """
                SELECT default_duration_minutes
                FROM tg_tenant_google_calendar_config
                WHERE tenant_id = @tenant_id
                LIMIT 1;
                """;
            durationCommand.Parameters.AddWithValue("@tenant_id", tenant);
            var durationScalar = await durationCommand.ExecuteScalarAsync();
            if (durationScalar is not null && durationScalar is not DBNull)
            {
                defaultJobDurationMinutes = Math.Clamp(
                    Convert.ToInt32(durationScalar, CultureInfo.InvariantCulture),
                    15,
                    720);
            }
        }

        if (hasLegacyBookings)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = hasGeocodeCache
                ? """
                  SELECT
                    b.id,
                    b.customer_phone,
                    b.customer_name,
                    b.service_category,
                    b.service_title,
                    b.start_local,
                    b.duration_minutes,
                    b.address,
                    b.technician_name,
                    b.created_at_utc,
                    g.latitude,
                    g.longitude
                  FROM tg_bookings b
                  LEFT JOIN tg_booking_geocode_cache g ON g.booking_id = b.id
                  WHERE b.tenant_id = @tenant_id
                  ORDER BY b.created_at_utc DESC
                  LIMIT @limit;
                  """
                : """
                  SELECT
                    b.id,
                    b.customer_phone,
                    b.customer_name,
                    b.service_category,
                    b.service_title,
                    b.start_local,
                    b.duration_minutes,
                    b.address,
                    b.technician_name,
                    b.created_at_utc,
                    NULL,
                    NULL
                  FROM tg_bookings b
                  WHERE b.tenant_id = @tenant_id
                  ORDER BY b.created_at_utc DESC
                  LIMIT @limit;
                  """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                output.Add(new BookingListItem
                {
                    Id = reader.GetString(0),
                    Status = "Legacy",
                    CustomerPhone = reader.GetString(1),
                    CustomerName = reader.GetString(2),
                    ServiceCategory = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ServiceTitle = reader.GetString(4),
                    StartLocal = ParseLocalDateTime(reader.GetString(5)),
                    DurationMinutes = reader.GetInt32(6),
                    Address = reader.GetString(7),
                    Latitude = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    Longitude = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    TechnicianName = reader.GetString(8),
                    CreatedAtUtc = ParseUtc(reader.GetString(9))
                });
            }
        }

        if (hasJobs)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = hasUsers && hasGeocodeCache
                ? """
                  SELECT
                    j.Id,
                    j.Category,
                    j.Description,
                    j.ScheduledAt,
                    j.AddressText,
                    j.CreatedAt,
                    j.Status,
                    c.Name,
                    c.Phone,
                    c.TelegramUserId,
                    p.Name,
                    COALESCE(j.Latitude, g.latitude),
                    COALESCE(j.Longitude, g.longitude)
                  FROM tg_Jobs j
                  LEFT JOIN tg_Users c ON c.Id = j.ClientUserId
                  LEFT JOIN tg_Users p ON p.Id = j.ProviderUserId
                  LEFT JOIN tg_booking_geocode_cache g
                    ON g.booking_id = ('job:' || j.Id)
                   AND g.tenant_id = j.TenantId
                  WHERE j.TenantId = @tenant_id
                    AND j.Status <> 'Draft'
                  ORDER BY j.CreatedAt DESC
                  LIMIT @limit;
                  """
                : hasUsers
                ? """
                  SELECT
                    j.Id,
                    j.Category,
                    j.Description,
                    j.ScheduledAt,
                    j.AddressText,
                    j.CreatedAt,
                    j.Status,
                    c.Name,
                    c.Phone,
                    c.TelegramUserId,
                    p.Name,
                    j.Latitude,
                    j.Longitude
                  FROM tg_Jobs j
                  LEFT JOIN tg_Users c ON c.Id = j.ClientUserId
                  LEFT JOIN tg_Users p ON p.Id = j.ProviderUserId
                  WHERE j.TenantId = @tenant_id
                    AND j.Status <> 'Draft'
                  ORDER BY j.CreatedAt DESC
                  LIMIT @limit;
                  """
                : """
                  SELECT
                    j.Id,
                    j.Category,
                    j.Description,
                    j.ScheduledAt,
                    j.AddressText,
                    j.CreatedAt,
                    j.Status,
                    '',
                    '',
                    NULL,
                    '',
                    j.Latitude,
                    j.Longitude
                  FROM tg_Jobs j
                  WHERE j.TenantId = @tenant_id
                    AND j.Status <> 'Draft'
                  ORDER BY j.CreatedAt DESC
                  LIMIT @limit;
                  """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var createdAtUtc = ParseUtc(reader.IsDBNull(5) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(5));
                var scheduledAtRaw = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var customerPhone = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
                var telegramUserId = reader.IsDBNull(9) ? 0L : reader.GetInt64(9);
                if (string.IsNullOrWhiteSpace(customerPhone))
                {
                    customerPhone = telegramUserId > 0 ? $"tg:{telegramUserId}" : string.Empty;
                }

                output.Add(new BookingListItem
                {
                    Id = $"job:{reader.GetInt64(0)}",
                    Status = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    CustomerPhone = customerPhone,
                    CustomerName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    ServiceCategory = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    ServiceTitle = TrimPreview(reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                    StartLocal = ParseLocalOrUtcDateTime(scheduledAtRaw, createdAtUtc),
                    DurationMinutes = defaultJobDurationMinutes,
                    Address = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Latitude = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    Longitude = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                    TechnicianName = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    CreatedAtUtc = createdAtUtc
                });
            }
        }

        return output
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(safeLimit)
            .ToList();
    }

    public async Task<BookingDetailsViewModel?> GetBookingDetailsAsync(string tenantId, string bookingId)
    {
        var tenant = NormalizeTenant(tenantId);
        var normalizedBookingId = (bookingId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedBookingId))
        {
            return null;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasLegacyBookings = await TableExistsAsync(connection, "tg_bookings");
        var hasJobs = await TableExistsAsync(connection, "tg_Jobs");
        var hasUsers = await TableExistsAsync(connection, "tg_Users");
        var hasClientProfiles = await TableExistsAsync(connection, "tg_client_profiles");
        var hasGeocodeCache = await TableExistsAsync(connection, "tg_booking_geocode_cache");
        var hasMessagesLog = await TableExistsAsync(connection, "tg_MessagesLog");
        var hasJobPhotos = await TableExistsAsync(connection, "tg_JobPhotos");
        var hasRatings = await TableExistsAsync(connection, "tg_Ratings");
        var defaultJobDurationMinutes = await GetDefaultJobDurationMinutesAsync(connection, tenant);

        async Task<(string Name, string Phone)> LoadUserContactAsync(long userId, bool preferClientProfile)
        {
            if (!hasUsers || userId <= 0)
            {
                return (Name: string.Empty, Phone: string.Empty);
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                  COALESCE(Name, ''),
                  COALESCE(Phone, ''),
                  TelegramUserId
                FROM tg_Users
                WHERE TenantId = @tenant_id
                  AND Id = @user_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@user_id", userId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return (Name: string.Empty, Phone: string.Empty);
            }

            var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var phone = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var telegramUserId = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            if (string.IsNullOrWhiteSpace(phone) && telegramUserId > 0)
            {
                phone = $"tg:{telegramUserId}";
            }

            if (preferClientProfile && hasClientProfiles)
            {
                await using var profileCommand = connection.CreateCommand();
                profileCommand.CommandText =
                    """
                    SELECT
                      COALESCE(full_name, ''),
                      COALESCE(phone_number, '')
                    FROM tg_client_profiles
                    WHERE tenant_id = @tenant_id
                      AND user_id = @user_id
                    LIMIT 1;
                    """;
                profileCommand.Parameters.AddWithValue("@tenant_id", tenant);
                profileCommand.Parameters.AddWithValue("@user_id", userId);

                await using var profileReader = await profileCommand.ExecuteReaderAsync();
                if (await profileReader.ReadAsync())
                {
                    var profileName = profileReader.IsDBNull(0) ? string.Empty : profileReader.GetString(0);
                    var profilePhone = profileReader.IsDBNull(1) ? string.Empty : profileReader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(profileName))
                    {
                        name = profileName;
                    }

                    if (!string.IsNullOrWhiteSpace(profilePhone))
                    {
                        phone = profilePhone;
                    }
                }
            }

            return (name, phone);
        }

        async Task<(double? Latitude, double? Longitude)> LoadCachedCoordinatesAsync(string cacheBookingId)
        {
            if (!hasGeocodeCache)
            {
                return (Latitude: (double?)null, Longitude: (double?)null);
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                  latitude,
                  longitude
                FROM tg_booking_geocode_cache
                WHERE tenant_id = @tenant_id
                  AND booking_id = @booking_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@booking_id", cacheBookingId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return (Latitude: (double?)null, Longitude: (double?)null);
            }

            var latitude = reader.IsDBNull(0) ? (double?)null : reader.GetDouble(0);
            var longitude = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
            return (latitude, longitude);
        }

        if (TryParseJobBookingId(normalizedBookingId, out var jobId) && hasJobs)
        {
            var hasContactName = await ColumnExistsAsync(connection, "tg_Jobs", "ContactName");
            var hasContactPhone = await ColumnExistsAsync(connection, "tg_Jobs", "ContactPhone");

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT
                  j.Id,
                  COALESCE(j.Category, ''),
                  COALESCE(j.Description, ''),
                  j.ScheduledAt,
                  COALESCE(j.AddressText, ''),
                  j.CreatedAt,
                  j.UpdatedAt,
                  COALESCE(j.Status, ''),
                  j.ClientUserId,
                  j.ProviderUserId,
                  j.Latitude,
                  j.Longitude,
                  COALESCE(j.PreferenceCode, ''),
                  j.IsUrgent,
                  {(hasContactName ? "j.ContactName" : "NULL")},
                  {(hasContactPhone ? "j.ContactPhone" : "NULL")},
                  j.FinalAmount,
                  COALESCE(j.FinalNotes, '')
                FROM tg_Jobs j
                WHERE j.TenantId = @tenant_id
                  AND j.Id = @job_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@job_id", jobId);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var createdAtUtc = ParseUtc(reader.IsDBNull(5) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(5));
                var updatedAtUtc = ParseUtc(reader.IsDBNull(6) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(6));
                var currentStatus = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
                var clientUserId = reader.IsDBNull(8) ? 0L : reader.GetInt64(8);
                var providerUserId = reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9);
                var latitude = reader.IsDBNull(10) ? (double?)null : reader.GetDouble(10);
                var longitude = reader.IsDBNull(11) ? (double?)null : reader.GetDouble(11);
                var customer = await LoadUserContactAsync(clientUserId, preferClientProfile: true);
                var provider = providerUserId.HasValue
                    ? await LoadUserContactAsync(providerUserId.Value, preferClientProfile: false)
                    : (Name: string.Empty, Phone: string.Empty);

                if ((!latitude.HasValue || !longitude.HasValue) && hasGeocodeCache)
                {
                    var cached = await LoadCachedCoordinatesAsync($"job:{jobId}");
                    latitude ??= cached.Latitude;
                    longitude ??= cached.Longitude;
                }

                var model = new BookingDetailsViewModel
                {
                    TenantId = tenant,
                    Id = normalizedBookingId,
                    IsLegacy = false,
                    Status = currentStatus,
                    StatusLabel = FormatBookingStatusLabel(currentStatus),
                    CustomerName = customer.Name,
                    CustomerPhone = customer.Phone,
                    ContactName = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                    ContactPhone = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                    ServiceCategory = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    ServiceTitle = TrimPreview(reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                    Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Address = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Latitude = latitude,
                    Longitude = longitude,
                    StartLocal = ParseLocalOrUtcDateTime(reader.IsDBNull(3) ? string.Empty : reader.GetString(3), createdAtUtc),
                    DurationMinutes = defaultJobDurationMinutes,
                    IsUrgent = ReadBool(reader, 13),
                    PreferenceCode = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                    TechnicianName = provider.Name,
                    TechnicianPhone = provider.Phone,
                    FinalAmount = reader.IsDBNull(16) ? null : Convert.ToDecimal(reader.GetValue(16), CultureInfo.InvariantCulture),
                    FinalNotes = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = updatedAtUtc
                };

                if (string.IsNullOrWhiteSpace(model.ContactName))
                {
                    model.ContactName = model.CustomerName;
                }

                if (string.IsNullOrWhiteSpace(model.ContactPhone))
                {
                    model.ContactPhone = model.CustomerPhone;
                }

                model.PreferenceLabel = ResolvePreferenceLabel(model.PreferenceCode);

                if (hasJobPhotos)
                {
                    model.PhotoCount = await QueryIntAsync(
                        connection,
                        """
                        SELECT COUNT(*)
                        FROM tg_JobPhotos
                        WHERE JobId = @job_id;
                        """,
                        ("@job_id", jobId));
                }

                if (hasRatings)
                {
                    await using var ratingCommand = connection.CreateCommand();
                    ratingCommand.CommandText =
                        """
                        SELECT
                          Stars
                        FROM tg_Ratings
                        WHERE JobId = @job_id
                        LIMIT 1;
                        """;
                    ratingCommand.Parameters.AddWithValue("@job_id", jobId);
                    var ratingScalar = await ratingCommand.ExecuteScalarAsync();
                    if (ratingScalar is not null and not DBNull)
                    {
                        model.RatingStars = Convert.ToInt32(ratingScalar, CultureInfo.InvariantCulture);
                    }
                }

                model.StatusHistory = hasMessagesLog
                    ? await GetJobStatusHistoryAsync(connection, tenant, jobId, currentStatus, createdAtUtc, updatedAtUtc)
                    : new[]
                    {
                        new BookingStatusHistoryItem
                        {
                            Status = string.IsNullOrWhiteSpace(currentStatus) ? "WaitingProvider" : currentStatus,
                            StatusLabel = FormatBookingStatusLabel(string.IsNullOrWhiteSpace(currentStatus) ? "WaitingProvider" : currentStatus),
                            Description = "Pedido criado.",
                            AtUtc = createdAtUtc
                        }
                    };

                return model;
            }
        }

        if (!hasLegacyBookings)
        {
            return null;
        }

        await using var legacyCommand = connection.CreateCommand();
        legacyCommand.CommandText =
            """
            SELECT
              id,
              COALESCE(customer_phone, ''),
              COALESCE(customer_name, ''),
              COALESCE(service_category, ''),
              COALESCE(service_title, ''),
              start_local,
              duration_minutes,
              COALESCE(address, ''),
              COALESCE(technician_name, ''),
              created_at_utc
            FROM tg_bookings
            WHERE tenant_id = @tenant_id
              AND id = @booking_id
            LIMIT 1;
            """;
        legacyCommand.Parameters.AddWithValue("@tenant_id", tenant);
        legacyCommand.Parameters.AddWithValue("@booking_id", normalizedBookingId);

        await using var legacyReader = await legacyCommand.ExecuteReaderAsync();
        if (!await legacyReader.ReadAsync())
        {
            return null;
        }

        var legacyCreatedAtUtc = ParseUtc(legacyReader.IsDBNull(9) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : legacyReader.GetString(9));
        var legacyCoordinates = await LoadCachedCoordinatesAsync(normalizedBookingId);

        return new BookingDetailsViewModel
        {
            TenantId = tenant,
            Id = normalizedBookingId,
            IsLegacy = true,
            Status = "Legacy",
            StatusLabel = FormatBookingStatusLabel("Legacy"),
            CustomerName = legacyReader.IsDBNull(2) ? string.Empty : legacyReader.GetString(2),
            CustomerPhone = legacyReader.IsDBNull(1) ? string.Empty : legacyReader.GetString(1),
            ContactName = legacyReader.IsDBNull(2) ? string.Empty : legacyReader.GetString(2),
            ContactPhone = legacyReader.IsDBNull(1) ? string.Empty : legacyReader.GetString(1),
            ServiceCategory = legacyReader.IsDBNull(3) ? string.Empty : legacyReader.GetString(3),
            ServiceTitle = legacyReader.IsDBNull(4) ? string.Empty : legacyReader.GetString(4),
            Description = legacyReader.IsDBNull(4) ? string.Empty : legacyReader.GetString(4),
            Address = legacyReader.IsDBNull(7) ? string.Empty : legacyReader.GetString(7),
            Latitude = legacyCoordinates.Latitude,
            Longitude = legacyCoordinates.Longitude,
            StartLocal = ParseLocalDateTime(legacyReader.IsDBNull(5) ? string.Empty : legacyReader.GetString(5)),
            DurationMinutes = legacyReader.IsDBNull(6) ? defaultJobDurationMinutes : legacyReader.GetInt32(6),
            PreferenceCode = string.Empty,
            PreferenceLabel = "Nao informado",
            TechnicianName = legacyReader.IsDBNull(8) ? string.Empty : legacyReader.GetString(8),
            TechnicianPhone = string.Empty,
            CreatedAtUtc = legacyCreatedAtUtc,
            UpdatedAtUtc = legacyCreatedAtUtc,
            StatusHistory = new[]
            {
                new BookingStatusHistoryItem
                {
                    Status = "Legacy",
                    StatusLabel = FormatBookingStatusLabel("Legacy"),
                    Description = "Registro legado importado para visualizacao.",
                    AtUtc = legacyCreatedAtUtc
                }
            }
        };
    }

    public async Task<IReadOnlyList<ClientListItem>> GetClientsAsync(string tenantId, int limit)
    {
        var output = new List<ClientListItem>();
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 1000);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasUsers = await TableExistsAsync(connection, "tg_Users");
        if (!hasUsers)
        {
            return output;
        }

        var hasJobs = await TableExistsAsync(connection, "tg_Jobs");
        var hasClientProfiles = await TableExistsAsync(connection, "tg_client_profiles");

        await using var command = connection.CreateCommand();
        command.CommandText = hasJobs
            ? """
              SELECT
                u.Id,
                u.TenantId,
                u.TelegramUserId,
                COALESCE(u.Name, ''),
                COALESCE(u.Username, ''),
                COALESCE(u.Phone, ''),
                COALESCE(u.Role, ''),
                COALESCE(u.IsActive, 1),
                u.CreatedAt,
                u.UpdatedAt,
                COALESCE(s.total_jobs, 0),
                COALESCE(s.open_jobs, 0),
                COALESCE(s.finished_jobs, 0),
                COALESCE(s.cancelled_jobs, 0),
                s.last_job_at
              FROM tg_Users u
              LEFT JOIN (
                SELECT
                  j.ClientUserId AS user_id,
                  COUNT(*) AS total_jobs,
                  SUM(CASE WHEN j.Status NOT IN ('Draft', 'Finished', 'Cancelled') THEN 1 ELSE 0 END) AS open_jobs,
                  SUM(CASE WHEN j.Status = 'Finished' THEN 1 ELSE 0 END) AS finished_jobs,
                  SUM(CASE WHEN j.Status = 'Cancelled' THEN 1 ELSE 0 END) AS cancelled_jobs,
                  MAX(j.CreatedAt) AS last_job_at
                FROM tg_Jobs j
                WHERE j.TenantId = @tenant_id
                  AND j.Status <> 'Draft'
                GROUP BY j.ClientUserId
              ) s
                ON s.user_id = u.Id
              WHERE u.TenantId = @tenant_id
                AND (
                  LOWER(COALESCE(u.Role, '')) IN ('client', 'both')
                  OR EXISTS (
                    SELECT 1
                    FROM tg_Jobs jx
                    WHERE jx.TenantId = u.TenantId
                      AND jx.ClientUserId = u.Id
                      AND jx.Status <> 'Draft'
                  )
                )
              ORDER BY COALESCE(s.last_job_at, u.UpdatedAt) DESC, u.Name ASC
              LIMIT @limit;
              """
            : """
              SELECT
                u.Id,
                u.TenantId,
                u.TelegramUserId,
                COALESCE(u.Name, ''),
                COALESCE(u.Username, ''),
                COALESCE(u.Phone, ''),
                COALESCE(u.Role, ''),
                COALESCE(u.IsActive, 1),
                u.CreatedAt,
                u.UpdatedAt,
                0,
                0,
                0,
                0,
                NULL
              FROM tg_Users u
              WHERE u.TenantId = @tenant_id
                AND LOWER(COALESCE(u.Role, '')) IN ('client', 'both')
              ORDER BY u.UpdatedAt DESC, u.Name ASC
              LIMIT @limit;
              """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@limit", safeLimit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.Add(new ClientListItem
            {
                Id = reader.GetInt64(0),
                TenantId = reader.IsDBNull(1) ? tenant : reader.GetString(1),
                TelegramUserId = reader.IsDBNull(2) ? 0L : reader.GetInt64(2),
                Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Username = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Phone = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Role = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                IsActive = reader.IsDBNull(7) || Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1,
                CreatedAtUtc = ParseUtc(reader.IsDBNull(8) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(8)),
                UpdatedAtUtc = ParseUtc(reader.IsDBNull(9) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(9)),
                TotalJobs = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture),
                OpenJobs = reader.IsDBNull(11) ? 0 : Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture),
                FinishedJobs = reader.IsDBNull(12) ? 0 : Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture),
                CancelledJobs = reader.IsDBNull(13) ? 0 : Convert.ToInt32(reader.GetValue(13), CultureInfo.InvariantCulture),
                LastJobAtUtc = TryParseNullableUtc(reader.IsDBNull(14) ? null : reader.GetString(14))
            });
        }

        if (hasClientProfiles && output.Count > 0)
        {
            var byId = output.ToDictionary(x => x.Id);
            await using var profileCommand = connection.CreateCommand();
            profileCommand.CommandText =
                """
                SELECT
                  user_id,
                  COALESCE(full_name, ''),
                  COALESCE(email, ''),
                  COALESCE(cpf, ''),
                  COALESCE(street, ''),
                  COALESCE(number, ''),
                  COALESCE(complement, ''),
                  COALESCE(neighborhood, ''),
                  COALESCE(city, ''),
                  COALESCE(state, ''),
                  COALESCE(cep, ''),
                  latitude,
                  longitude,
                  COALESCE(phone_number, ''),
                  COALESCE(is_registration_complete, 0),
                  updated_at_utc
                FROM tg_client_profiles
                WHERE tenant_id = @tenant_id;
                """;
            profileCommand.Parameters.AddWithValue("@tenant_id", tenant);

            await using var profileReader = await profileCommand.ExecuteReaderAsync();
            while (await profileReader.ReadAsync())
            {
                var userId = profileReader.IsDBNull(0) ? 0L : profileReader.GetInt64(0);
                if (!byId.TryGetValue(userId, out var item))
                {
                    continue;
                }

                var fullName = profileReader.IsDBNull(1) ? string.Empty : profileReader.GetString(1);
                var phoneNumber = profileReader.IsDBNull(13) ? string.Empty : profileReader.GetString(13);
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    item.Name = fullName;
                }

                if (!string.IsNullOrWhiteSpace(phoneNumber))
                {
                    item.Phone = phoneNumber;
                }

                item.Email = profileReader.IsDBNull(2) ? string.Empty : profileReader.GetString(2);
                item.Cpf = profileReader.IsDBNull(3) ? string.Empty : profileReader.GetString(3);
                item.Street = profileReader.IsDBNull(4) ? string.Empty : profileReader.GetString(4);
                item.Number = profileReader.IsDBNull(5) ? string.Empty : profileReader.GetString(5);
                item.Complement = profileReader.IsDBNull(6) ? string.Empty : profileReader.GetString(6);
                item.Neighborhood = profileReader.IsDBNull(7) ? string.Empty : profileReader.GetString(7);
                item.City = profileReader.IsDBNull(8) ? string.Empty : profileReader.GetString(8);
                item.State = profileReader.IsDBNull(9) ? string.Empty : profileReader.GetString(9);
                item.Cep = profileReader.IsDBNull(10) ? string.Empty : profileReader.GetString(10);
                item.Latitude = profileReader.IsDBNull(11) ? null : profileReader.GetDouble(11);
                item.Longitude = profileReader.IsDBNull(12) ? null : profileReader.GetDouble(12);
                item.IsRegistrationComplete = !profileReader.IsDBNull(14) && Convert.ToInt32(profileReader.GetValue(14), CultureInfo.InvariantCulture) == 1;

                var profileUpdatedAt = TryParseNullableUtc(profileReader.IsDBNull(15) ? null : profileReader.GetString(15));
                if (profileUpdatedAt.HasValue && profileUpdatedAt.Value > item.UpdatedAtUtc)
                {
                    item.UpdatedAtUtc = profileUpdatedAt.Value;
                }
            }
        }

        return output;
    }

    public async Task<IReadOnlyList<ProviderListItem>> GetProvidersAsync(string tenantId, int limit)
    {
        var output = new List<ProviderListItem>();
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 1000);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasUsers = await TableExistsAsync(connection, "tg_Users");
        if (!hasUsers)
        {
            return output;
        }

        var hasJobs = await TableExistsAsync(connection, "tg_Jobs");
        var hasProviderProfiles = await TableExistsAsync(connection, "tg_ProvidersProfile");

        await using var command = connection.CreateCommand();
        command.CommandText = hasJobs && hasProviderProfiles
            ? """
              SELECT
                u.Id,
                u.TenantId,
                u.TelegramUserId,
                COALESCE(u.Name, ''),
                COALESCE(u.Username, ''),
                COALESCE(u.Phone, ''),
                COALESCE(u.Role, ''),
                COALESCE(u.IsActive, 1),
                COALESCE(p.IsAvailable, 1),
                COALESCE(p.CategoriesJson, '[]'),
                COALESCE(p.RadiusKm, 10),
                COALESCE(p.AvgRating, 0),
                COALESCE(p.TotalReviews, 0),
                p.BaseLatitude,
                p.BaseLongitude,
                COALESCE(s.total_jobs, 0),
                COALESCE(s.open_jobs, 0),
                COALESCE(s.finished_jobs, 0),
                COALESCE(s.cancelled_jobs, 0),
                s.last_job_at,
                u.CreatedAt,
                u.UpdatedAt
              FROM tg_Users u
              LEFT JOIN tg_ProvidersProfile p ON p.UserId = u.Id
              LEFT JOIN (
                SELECT
                  j.ProviderUserId AS user_id,
                  COUNT(*) AS total_jobs,
                  SUM(CASE WHEN j.Status NOT IN ('Draft', 'Finished', 'Cancelled') THEN 1 ELSE 0 END) AS open_jobs,
                  SUM(CASE WHEN j.Status = 'Finished' THEN 1 ELSE 0 END) AS finished_jobs,
                  SUM(CASE WHEN j.Status = 'Cancelled' THEN 1 ELSE 0 END) AS cancelled_jobs,
                  MAX(j.CreatedAt) AS last_job_at
                FROM tg_Jobs j
                WHERE j.TenantId = @tenant_id
                  AND j.ProviderUserId IS NOT NULL
                  AND j.Status <> 'Draft'
                GROUP BY j.ProviderUserId
              ) s
                ON s.user_id = u.Id
              WHERE u.TenantId = @tenant_id
                AND (
                  LOWER(COALESCE(u.Role, '')) IN ('provider', 'both')
                  OR p.UserId IS NOT NULL
                  OR EXISTS (
                    SELECT 1
                    FROM tg_Jobs jx
                    WHERE jx.TenantId = u.TenantId
                      AND jx.ProviderUserId = u.Id
                      AND jx.Status <> 'Draft'
                  )
                )
              ORDER BY COALESCE(s.last_job_at, u.UpdatedAt) DESC, u.Name ASC
              LIMIT @limit;
              """
            : hasProviderProfiles
            ? """
              SELECT
                u.Id,
                u.TenantId,
                u.TelegramUserId,
                COALESCE(u.Name, ''),
                COALESCE(u.Username, ''),
                COALESCE(u.Phone, ''),
                COALESCE(u.Role, ''),
                COALESCE(u.IsActive, 1),
                COALESCE(p.IsAvailable, 1),
                COALESCE(p.CategoriesJson, '[]'),
                COALESCE(p.RadiusKm, 10),
                COALESCE(p.AvgRating, 0),
                COALESCE(p.TotalReviews, 0),
                p.BaseLatitude,
                p.BaseLongitude,
                0,
                0,
                0,
                0,
                NULL,
                u.CreatedAt,
                u.UpdatedAt
              FROM tg_Users u
              LEFT JOIN tg_ProvidersProfile p ON p.UserId = u.Id
              WHERE u.TenantId = @tenant_id
                AND (
                  LOWER(COALESCE(u.Role, '')) IN ('provider', 'both')
                  OR p.UserId IS NOT NULL
                )
              ORDER BY u.UpdatedAt DESC, u.Name ASC
              LIMIT @limit;
              """
            : hasJobs
            ? """
              SELECT
                u.Id,
                u.TenantId,
                u.TelegramUserId,
                COALESCE(u.Name, ''),
                COALESCE(u.Username, ''),
                COALESCE(u.Phone, ''),
                COALESCE(u.Role, ''),
                COALESCE(u.IsActive, 1),
                1,
                '[]',
                10,
                0,
                0,
                NULL,
                NULL,
                COALESCE(s.total_jobs, 0),
                COALESCE(s.open_jobs, 0),
                COALESCE(s.finished_jobs, 0),
                COALESCE(s.cancelled_jobs, 0),
                s.last_job_at,
                u.CreatedAt,
                u.UpdatedAt
              FROM tg_Users u
              LEFT JOIN (
                SELECT
                  j.ProviderUserId AS user_id,
                  COUNT(*) AS total_jobs,
                  SUM(CASE WHEN j.Status NOT IN ('Draft', 'Finished', 'Cancelled') THEN 1 ELSE 0 END) AS open_jobs,
                  SUM(CASE WHEN j.Status = 'Finished' THEN 1 ELSE 0 END) AS finished_jobs,
                  SUM(CASE WHEN j.Status = 'Cancelled' THEN 1 ELSE 0 END) AS cancelled_jobs,
                  MAX(j.CreatedAt) AS last_job_at
                FROM tg_Jobs j
                WHERE j.TenantId = @tenant_id
                  AND j.ProviderUserId IS NOT NULL
                  AND j.Status <> 'Draft'
                GROUP BY j.ProviderUserId
              ) s
                ON s.user_id = u.Id
              WHERE u.TenantId = @tenant_id
                AND (
                  LOWER(COALESCE(u.Role, '')) IN ('provider', 'both')
                  OR EXISTS (
                    SELECT 1
                    FROM tg_Jobs jx
                    WHERE jx.TenantId = u.TenantId
                      AND jx.ProviderUserId = u.Id
                      AND jx.Status <> 'Draft'
                  )
                )
              ORDER BY COALESCE(s.last_job_at, u.UpdatedAt) DESC, u.Name ASC
              LIMIT @limit;
              """
            : """
              SELECT
                u.Id,
                u.TenantId,
                u.TelegramUserId,
                COALESCE(u.Name, ''),
                COALESCE(u.Username, ''),
                COALESCE(u.Phone, ''),
                COALESCE(u.Role, ''),
                COALESCE(u.IsActive, 1),
                1,
                '[]',
                10,
                0,
                0,
                NULL,
                NULL,
                0,
                0,
                0,
                0,
                NULL,
                u.CreatedAt,
                u.UpdatedAt
              FROM tg_Users u
              WHERE u.TenantId = @tenant_id
                AND LOWER(COALESCE(u.Role, '')) IN ('provider', 'both')
              ORDER BY u.UpdatedAt DESC, u.Name ASC
              LIMIT @limit;
              """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@limit", safeLimit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.Add(new ProviderListItem
            {
                Id = reader.GetInt64(0),
                TenantId = reader.IsDBNull(1) ? tenant : reader.GetString(1),
                TelegramUserId = reader.IsDBNull(2) ? 0L : reader.GetInt64(2),
                Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Username = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Phone = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Role = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                IsActive = reader.IsDBNull(7) || Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1,
                IsAvailable = reader.IsDBNull(8) || Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) == 1,
                CategoriesSummary = ParseCategoriesSummary(reader.IsDBNull(9) ? "[]" : reader.GetString(9)),
                RadiusKm = reader.IsDBNull(10) ? 10 : Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture),
                AvgRating = reader.IsDBNull(11) ? 0m : Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture),
                TotalReviews = reader.IsDBNull(12) ? 0 : Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture),
                BaseLatitude = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                BaseLongitude = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                TotalJobs = reader.IsDBNull(15) ? 0 : Convert.ToInt32(reader.GetValue(15), CultureInfo.InvariantCulture),
                OpenJobs = reader.IsDBNull(16) ? 0 : Convert.ToInt32(reader.GetValue(16), CultureInfo.InvariantCulture),
                FinishedJobs = reader.IsDBNull(17) ? 0 : Convert.ToInt32(reader.GetValue(17), CultureInfo.InvariantCulture),
                CancelledJobs = reader.IsDBNull(18) ? 0 : Convert.ToInt32(reader.GetValue(18), CultureInfo.InvariantCulture),
                LastJobAtUtc = TryParseNullableUtc(reader.IsDBNull(19) ? null : reader.GetString(19)),
                CreatedAtUtc = ParseUtc(reader.IsDBNull(20) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(20)),
                UpdatedAtUtc = ParseUtc(reader.IsDBNull(21) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(21))
            });
        }

        return output;
    }

    public async Task<IReadOnlyList<ProviderCoverageItem>> GetProviderCoverageAsync(string tenantId, int limit)
    {
        var output = new List<ProviderCoverageItem>();
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 2000);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var hasUsers = await TableExistsAsync(connection, "tg_Users");
        if (!hasUsers)
        {
            return output;
        }

        var hasProviderProfiles = await TableExistsAsync(connection, "tg_ProvidersProfile");
        var hasJobs = await TableExistsAsync(connection, "tg_Jobs");
        var hasGeocodeCache = hasJobs && await TableExistsAsync(connection, "tg_booking_geocode_cache");

        await using (var providerCommand = connection.CreateCommand())
        {
            providerCommand.CommandText = hasProviderProfiles
                ? """
                  SELECT
                    u.Id,
                    u.TelegramUserId,
                    COALESCE(u.Name, ''),
                    COALESCE(u.Username, ''),
                    COALESCE(u.Phone, ''),
                    COALESCE(p.RadiusKm, 10),
                    p.BaseLatitude,
                    p.BaseLongitude
                  FROM tg_Users u
                  LEFT JOIN tg_ProvidersProfile p ON p.UserId = u.Id
                  WHERE u.TenantId = @tenant_id
                    AND (
                      LOWER(COALESCE(u.Role, '')) IN ('provider', 'both')
                      OR p.UserId IS NOT NULL
                    )
                  ORDER BY u.Name ASC, u.Id ASC
                  LIMIT @limit;
                  """
                : """
                  SELECT
                    u.Id,
                    u.TelegramUserId,
                    COALESCE(u.Name, ''),
                    COALESCE(u.Username, ''),
                    COALESCE(u.Phone, ''),
                    10,
                    NULL,
                    NULL
                  FROM tg_Users u
                  WHERE u.TenantId = @tenant_id
                    AND LOWER(COALESCE(u.Role, '')) IN ('provider', 'both')
                  ORDER BY u.Name ASC, u.Id ASC
                  LIMIT @limit;
                  """;
            providerCommand.Parameters.AddWithValue("@tenant_id", tenant);
            providerCommand.Parameters.AddWithValue("@limit", safeLimit);

            await using var providerReader = await providerCommand.ExecuteReaderAsync();
            while (await providerReader.ReadAsync())
            {
                output.Add(new ProviderCoverageItem
                {
                    ProviderUserId = providerReader.GetInt64(0),
                    TelegramUserId = providerReader.IsDBNull(1) ? 0L : providerReader.GetInt64(1),
                    Name = providerReader.IsDBNull(2) ? string.Empty : providerReader.GetString(2),
                    Username = providerReader.IsDBNull(3) ? string.Empty : providerReader.GetString(3),
                    Phone = providerReader.IsDBNull(4) ? string.Empty : providerReader.GetString(4),
                    RadiusKm = providerReader.IsDBNull(5) ? 10 : Convert.ToInt32(providerReader.GetValue(5), CultureInfo.InvariantCulture),
                    BaseLatitude = providerReader.IsDBNull(6) ? null : providerReader.GetDouble(6),
                    BaseLongitude = providerReader.IsDBNull(7) ? null : providerReader.GetDouble(7),
                    Neighborhoods = Array.Empty<string>()
                });
            }
        }

        if (!hasJobs || output.Count == 0)
        {
            return output;
        }

        var jobs = new List<(long? ProviderUserId, string Neighborhood, double Latitude, double Longitude)>();
        var reverseLookupBudget = CoverageReverseLookupBudget;

        await using (var jobsCommand = connection.CreateCommand())
        {
            jobsCommand.CommandText = hasGeocodeCache
                ?
                """
                SELECT
                  j.ProviderUserId,
                  COALESCE(j.AddressText, ''),
                  COALESCE(j.Latitude, g.latitude),
                  COALESCE(j.Longitude, g.longitude)
                FROM tg_Jobs j
                LEFT JOIN tg_booking_geocode_cache g
                  ON g.booking_id = ('job:' || j.Id)
                 AND g.tenant_id = j.TenantId
                WHERE j.TenantId = @tenant_id
                  AND j.Status <> 'Draft'
                ORDER BY j.CreatedAt DESC
                """
                : """
                SELECT
                  j.ProviderUserId,
                  COALESCE(j.AddressText, ''),
                  j.Latitude,
                  j.Longitude
                FROM tg_Jobs j
                WHERE j.TenantId = @tenant_id
                  AND j.Status <> 'Draft'
                ORDER BY j.CreatedAt DESC
                """;
            jobsCommand.Parameters.AddWithValue("@tenant_id", tenant);

            await using var jobsReader = await jobsCommand.ExecuteReaderAsync();
            while (await jobsReader.ReadAsync())
            {
                var latitude = jobsReader.IsDBNull(2)
                    ? (double?)null
                    : Convert.ToDouble(jobsReader.GetValue(2), CultureInfo.InvariantCulture);
                var longitude = jobsReader.IsDBNull(3)
                    ? (double?)null
                    : Convert.ToDouble(jobsReader.GetValue(3), CultureInfo.InvariantCulture);

                if (!latitude.HasValue || !longitude.HasValue)
                {
                    continue;
                }

                var neighborhood = ExtractNeighborhoodFromAddress(jobsReader.IsDBNull(1) ? string.Empty : jobsReader.GetString(1));
                if (string.IsNullOrWhiteSpace(neighborhood) && reverseLookupBudget > 0)
                {
                    neighborhood = await TryReverseGeocodeNeighborhoodAsync(latitude.Value, longitude.Value);
                    reverseLookupBudget -= 1;
                }

                jobs.Add((
                    ProviderUserId: jobsReader.IsDBNull(0) ? null : jobsReader.GetInt64(0),
                    Neighborhood: neighborhood,
                    Latitude: latitude.Value,
                    Longitude: longitude.Value));
            }
        }

        foreach (var item in output)
        {
            var neighborhoods = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item.BaseLatitude.HasValue && item.BaseLongitude.HasValue)
            {
                var baseLat = item.BaseLatitude.Value;
                var baseLon = item.BaseLongitude.Value;
                var radiusKm = Math.Max(1, item.RadiusKm);

                foreach (var job in jobs)
                {
                    if (string.IsNullOrWhiteSpace(job.Neighborhood))
                    {
                        continue;
                    }

                    if (IsWithinCoverageRadius(baseLat, baseLon, radiusKm, job.Latitude, job.Longitude))
                    {
                        neighborhoods.Add(job.Neighborhood);
                    }
                }
            }

            // fallback para prestadores sem base/radius consistente: usa historico do proprio prestador.
            if (neighborhoods.Count == 0)
            {
                foreach (var job in jobs)
                {
                    if (job.ProviderUserId == item.ProviderUserId && !string.IsNullOrWhiteSpace(job.Neighborhood))
                    {
                        neighborhoods.Add(job.Neighborhood);
                    }
                }
            }

            if (neighborhoods.Count == 0 && item.BaseLatitude.HasValue && item.BaseLongitude.HasValue)
            {
                var baseNeighborhood = await TryReverseGeocodeNeighborhoodAsync(item.BaseLatitude.Value, item.BaseLongitude.Value);
                if (!string.IsNullOrWhiteSpace(baseNeighborhood))
                {
                    neighborhoods.Add(baseNeighborhood);
                }
            }

            item.Neighborhoods = neighborhoods.ToArray();
        }

        return output;
    }

    public async Task<IReadOnlyList<CategoryItem>> GetCategoriesAsync(string tenantId)
    {
        var output = new List<CategoryItem>();
        var tenant = NormalizeTenant(tenantId);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, name, normalized_name, created_at_utc
            FROM tg_service_categories
            WHERE tenant_id = @tenant_id
            ORDER BY name ASC;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.Add(new CategoryItem
            {
                Id = reader.GetInt64(0),
                TenantId = reader.GetString(1),
                Name = reader.GetString(2),
                NormalizedName = reader.IsDBNull(3) ? NormalizeCategoryKey(reader.GetString(2)) : reader.GetString(3),
                CreatedAtUtc = ParseUtc(reader.GetString(4))
            });
        }

        return output;
    }

    public async Task<CategoryItem?> GetCategoryByIdAsync(string tenantId, long id)
    {
        var tenant = NormalizeTenant(tenantId);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, name, normalized_name, created_at_utc
            FROM tg_service_categories
            WHERE tenant_id = @tenant_id AND id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new CategoryItem
        {
            Id = reader.GetInt64(0),
            TenantId = reader.GetString(1),
            Name = reader.GetString(2),
            NormalizedName = reader.IsDBNull(3) ? NormalizeCategoryKey(reader.GetString(2)) : reader.GetString(3),
            CreatedAtUtc = ParseUtc(reader.GetString(4))
        };
    }

    public async Task<CategoryItem> CreateCategoryAsync(string tenantId, string name)
    {
        var tenant = NormalizeTenant(tenantId);
        var safeName = BuildSafeCategoryName(name);
        var normalized = NormalizeCategoryKey(safeName);
        var now = DateTimeOffset.UtcNow;

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO tg_service_categories (tenant_id, name, normalized_name, created_at_utc)
                VALUES (@tenant_id, @name, @normalized_name, @created_at_utc)
                ON CONFLICT(tenant_id, normalized_name) DO UPDATE SET
                    name = excluded.name;
                """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@name", safeName);
            command.Parameters.AddWithValue("@normalized_name", normalized);
            command.Parameters.AddWithValue("@created_at_utc", ToUtcText(now));
            await command.ExecuteNonQueryAsync();
        }

        return await FindCategoryByNormalizedAsync(connection, tenant, normalized)
               ?? throw new InvalidOperationException("Nao foi possivel criar categoria.");
    }

    public async Task<CategoryItem?> UpdateCategoryAsync(string tenantId, long id, string name)
    {
        var tenant = NormalizeTenant(tenantId);
        var safeName = BuildSafeCategoryName(name);
        var normalized = NormalizeCategoryKey(safeName);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE tg_service_categories
                SET name = @name,
                    normalized_name = @normalized_name
                WHERE tenant_id = @tenant_id AND id = @id;
                """;
            command.Parameters.AddWithValue("@name", safeName);
            command.Parameters.AddWithValue("@normalized_name", normalized);
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@id", id);
            var affected = await command.ExecuteNonQueryAsync();
            if (affected <= 0)
            {
                return null;
            }
        }

        await using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT id, tenant_id, name, normalized_name, created_at_utc
            FROM tg_service_categories
            WHERE tenant_id = @tenant_id AND id = @id
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("@tenant_id", tenant);
        select.Parameters.AddWithValue("@id", id);

        await using var reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new CategoryItem
        {
            Id = reader.GetInt64(0),
            TenantId = reader.GetString(1),
            Name = reader.GetString(2),
            NormalizedName = reader.IsDBNull(3) ? NormalizeCategoryKey(reader.GetString(2)) : reader.GetString(3),
            CreatedAtUtc = ParseUtc(reader.GetString(4))
        };
    }

    public async Task<bool> DeleteCategoryAsync(string tenantId, long id)
    {
        var tenant = NormalizeTenant(tenantId);
        var current = await GetCategoryByIdAsync(tenant, id);
        if (current is null)
        {
            return false;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using (var updateServices = connection.CreateCommand())
        {
            updateServices.CommandText =
                """
                UPDATE tg_service_catalog
                SET category_name = 'Sem Categoria'
                WHERE tenant_id = @tenant_id AND category_name = @category_name;
                """;
            updateServices.Parameters.AddWithValue("@tenant_id", tenant);
            updateServices.Parameters.AddWithValue("@category_name", current.Name);
            await updateServices.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                DELETE FROM tg_service_categories
                WHERE tenant_id = @tenant_id AND id = @id;
                """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@id", id);
            var affected = await command.ExecuteNonQueryAsync();
            return affected > 0;
        }
    }

    public async Task<IReadOnlyList<ServiceCatalogItem>> GetServicesAsync(string tenantId)
    {
        var output = new List<ServiceCatalogItem>();
        var tenant = NormalizeTenant(tenantId);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, title, category_name, default_duration_minutes, is_active, created_at_utc, updated_at_utc
            FROM tg_service_catalog
            WHERE tenant_id = @tenant_id
            ORDER BY title ASC;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.Add(new ServiceCatalogItem
            {
                Id = reader.GetInt64(0),
                TenantId = reader.GetString(1),
                Title = reader.GetString(2),
                CategoryName = reader.GetString(3),
                DefaultDurationMinutes = reader.GetInt32(4),
                IsActive = reader.GetInt32(5) == 1,
                CreatedAtUtc = ParseUtc(reader.GetString(6)),
                UpdatedAtUtc = ParseUtc(reader.GetString(7))
            });
        }

        return output;
    }

    public async Task<ServiceCatalogItem?> GetServiceByIdAsync(string tenantId, long id)
    {
        var tenant = NormalizeTenant(tenantId);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, title, category_name, default_duration_minutes, is_active, created_at_utc, updated_at_utc
            FROM tg_service_catalog
            WHERE tenant_id = @tenant_id AND id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ServiceCatalogItem
        {
            Id = reader.GetInt64(0),
            TenantId = reader.GetString(1),
            Title = reader.GetString(2),
            CategoryName = reader.GetString(3),
            DefaultDurationMinutes = reader.GetInt32(4),
            IsActive = reader.GetInt32(5) == 1,
            CreatedAtUtc = ParseUtc(reader.GetString(6)),
            UpdatedAtUtc = ParseUtc(reader.GetString(7))
        };
    }

    public async Task<ServiceCatalogItem> CreateServiceAsync(ServiceEditViewModel input)
    {
        var tenant = NormalizeTenant(input.TenantId);
        var title = BuildSafeServiceTitle(input.Title);
        var category = await CreateCategoryAsync(tenant, input.CategoryName);
        var duration = Math.Clamp(input.DefaultDurationMinutes, 15, 600);
        var now = DateTimeOffset.UtcNow;

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO tg_service_catalog
                (tenant_id, title, category_name, default_duration_minutes, is_active, created_at_utc, updated_at_utc)
                VALUES
                (@tenant_id, @title, @category_name, @default_duration_minutes, @is_active, @created_at_utc, @updated_at_utc);
                """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@category_name", category.Name);
            command.Parameters.AddWithValue("@default_duration_minutes", duration);
            command.Parameters.AddWithValue("@is_active", input.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("@created_at_utc", ToUtcText(now));
            command.Parameters.AddWithValue("@updated_at_utc", ToUtcText(now));
            await command.ExecuteNonQueryAsync();
        }

        await using var lastIdCommand = connection.CreateCommand();
        lastIdCommand.CommandText = "SELECT last_insert_rowid();";
        var id = Convert.ToInt64(await lastIdCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        return await GetServiceByIdAsync(tenant, id)
               ?? throw new InvalidOperationException("Nao foi possivel criar servico.");
    }

    public async Task<ServiceCatalogItem?> UpdateServiceAsync(ServiceEditViewModel input)
    {
        if (!input.Id.HasValue)
        {
            return null;
        }

        var tenant = NormalizeTenant(input.TenantId);
        var title = BuildSafeServiceTitle(input.Title);
        var category = await CreateCategoryAsync(tenant, input.CategoryName);
        var duration = Math.Clamp(input.DefaultDurationMinutes, 15, 600);
        var now = DateTimeOffset.UtcNow;

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE tg_service_catalog
                SET title = @title,
                    category_name = @category_name,
                    default_duration_minutes = @default_duration_minutes,
                    is_active = @is_active,
                    updated_at_utc = @updated_at_utc
                WHERE tenant_id = @tenant_id AND id = @id;
                """;
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@category_name", category.Name);
            command.Parameters.AddWithValue("@default_duration_minutes", duration);
            command.Parameters.AddWithValue("@is_active", input.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("@updated_at_utc", ToUtcText(now));
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@id", input.Id.Value);
            var affected = await command.ExecuteNonQueryAsync();
            if (affected <= 0)
            {
                return null;
            }
        }

        return await GetServiceByIdAsync(tenant, input.Id.Value);
    }

    public async Task<bool> DeleteServiceAsync(string tenantId, long id)
    {
        var tenant = NormalizeTenant(tenantId);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM tg_service_catalog
            WHERE tenant_id = @tenant_id AND id = @id;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@id", id);

        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<BotConfigViewModel> GetBotConfigAsync(string tenantId)
    {
        var tenant = NormalizeTenant(tenantId);
        var model = BuildDefaultBotConfig(tenant);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT menu_json, messages_json
            FROM tg_tenant_bot_config
            WHERE tenant_id = @tenant_id
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var menuJson = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var messagesJson = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

                var menu = DeserializeMenu(menuJson);
                var messages = DeserializeMessages(messagesJson);
                model.MainMenuText = menu.MainMenuText;
                model.GreetingText = messages.GreetingText;
                model.HumanHandoffText = messages.HumanHandoffText;
                model.CloseConfirmationText = messages.CloseConfirmationText;
                model.ClosingText = messages.ClosingText;
                model.FallbackText = messages.FallbackText;
                model.MessagePoolingSeconds = ClampPoolingSeconds(messages.MessagePoolingSeconds);
                model.ProviderReminderEnabled = messages.ProviderReminderEnabled ?? true;
                model.ProviderReminderSweepIntervalMinutes = ClampProviderReminderSweepIntervalMinutes(messages.ProviderReminderSweepIntervalMinutes);
                model.ProviderReminderResendCooldownMinutes = ClampProviderReminderResendCooldownMinutes(messages.ProviderReminderResendCooldownMinutes);
                model.ProviderReminderSnoozeHours = ClampProviderReminderSnoozeHours(messages.ProviderReminderSnoozeHours);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT bot_id, bot_username, bot_token, is_active, polling_timeout_seconds, last_update_id
            FROM tg_tenant_telegram_config
            WHERE tenant_id = @tenant_id
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                model.TelegramBotId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                model.TelegramBotUsername = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                model.TelegramBotToken = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                model.TelegramIsActive = !reader.IsDBNull(3) && reader.GetInt32(3) == 1;
                model.TelegramPollingTimeoutSeconds = ClampTelegramPollingSeconds(reader.IsDBNull(4) ? 30 : reader.GetInt32(4));
                model.TelegramLastUpdateId = reader.IsDBNull(5) ? 0L : reader.GetInt64(5);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT
                is_enabled,
                calendar_id,
                service_account_json,
                time_zone_id,
                default_duration_minutes,
                availability_window_days,
                availability_slot_interval_minutes,
                availability_workday_start_hour,
                availability_workday_end_hour,
                availability_today_lead_minutes,
                max_attempts,
                retry_base_seconds,
                retry_max_seconds,
                event_title_template,
                event_description_template
            FROM tg_tenant_google_calendar_config
            WHERE tenant_id = @tenant_id
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                model.GoogleCalendarEnabled = !reader.IsDBNull(0) && reader.GetInt32(0) == 1;
                model.GoogleCalendarId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var serviceAccountJson = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                model.HasGoogleCalendarServiceAccountJson = !string.IsNullOrWhiteSpace(serviceAccountJson);
                model.GoogleCalendarServiceAccountJson = string.Empty;
                model.GoogleCalendarTimeZoneId = reader.IsDBNull(3) ? "America/Sao_Paulo" : reader.GetString(3);
                model.GoogleCalendarDefaultDurationMinutes = ClampGoogleCalendarDuration(reader.IsDBNull(4) ? 60 : reader.GetInt32(4));
                model.GoogleCalendarAvailabilityWindowDays = ClampGoogleCalendarAvailabilityWindowDays(reader.IsDBNull(5) ? 7 : reader.GetInt32(5));
                model.GoogleCalendarAvailabilitySlotIntervalMinutes = ClampGoogleCalendarAvailabilitySlotIntervalMinutes(reader.IsDBNull(6) ? 60 : reader.GetInt32(6));
                model.GoogleCalendarAvailabilityWorkdayStartHour = ClampGoogleCalendarAvailabilityWorkdayStartHour(reader.IsDBNull(7) ? 8 : reader.GetInt32(7));
                model.GoogleCalendarAvailabilityWorkdayEndHour = ClampGoogleCalendarAvailabilityWorkdayEndHour(reader.IsDBNull(8) ? 20 : reader.GetInt32(8), model.GoogleCalendarAvailabilityWorkdayStartHour);
                model.GoogleCalendarAvailabilityTodayLeadMinutes = ClampGoogleCalendarAvailabilityTodayLeadMinutes(reader.IsDBNull(9) ? 30 : reader.GetInt32(9));
                model.GoogleCalendarMaxAttempts = ClampGoogleCalendarMaxAttempts(reader.IsDBNull(10) ? 8 : reader.GetInt32(10));
                model.GoogleCalendarRetryBaseSeconds = ClampGoogleCalendarRetryBaseSeconds(reader.IsDBNull(11) ? 10 : reader.GetInt32(11));
                model.GoogleCalendarRetryMaxSeconds = ClampGoogleCalendarRetryMaxSeconds(reader.IsDBNull(12) ? 600 : reader.GetInt32(12));
                model.GoogleCalendarEventTitleTemplate = reader.IsDBNull(13) ? string.Empty : reader.GetString(13);
                model.GoogleCalendarEventDescriptionTemplate = reader.IsDBNull(14) ? string.Empty : reader.GetString(14);
            }
        }

        var openAiApiKey = await GetSharedSettingAsync(connection, OpenAiApiKeySettingKey);
        model.HasOpenAiApiKey = !string.IsNullOrWhiteSpace(openAiApiKey);
        model.OpenAiApiKey = string.Empty;

        return model;
    }

    public async Task<IReadOnlyList<TelegramUserOption>> GetTelegramUsersAsync(string tenantId, int limit = 200)
    {
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var output = new List<TelegramUserOption>();
        var knownIds = new HashSet<long>();

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        if (await TableExistsAsync(connection, "tg_Users"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
            """
            SELECT TelegramUserId, Name, Username, Role
            FROM tg_Users
            WHERE TenantId = @tenant_id
            ORDER BY UpdatedAt DESC
            LIMIT @limit;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var telegramUserId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                if (telegramUserId <= 0 || !knownIds.Add(telegramUserId))
                {
                    continue;
                }

                var name = reader.IsDBNull(1) ? "Usuario Telegram" : reader.GetString(1).Trim();
                var username = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                var role = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                var usernameText = string.IsNullOrWhiteSpace(username) ? string.Empty : $" @{username}";
                var roleText = string.IsNullOrWhiteSpace(role) ? string.Empty : $" [{role}]";

                output.Add(new TelegramUserOption
                {
                    TelegramUserId = telegramUserId,
                    DisplayLabel = $"{name}{usernameText} ({telegramUserId}){roleText}"
                });
            }
        }

        if (await TableExistsAsync(connection, "tg_MessagesLog"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
            """
            SELECT TelegramUserId, MAX(CreatedAt) AS last_seen
            FROM tg_MessagesLog
            WHERE TenantId = @tenant_id
            GROUP BY TelegramUserId
            ORDER BY last_seen DESC
            LIMIT @limit;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var telegramUserId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                if (telegramUserId <= 0 || !knownIds.Add(telegramUserId))
                {
                    continue;
                }

                output.Add(new TelegramUserOption
                {
                    TelegramUserId = telegramUserId,
                    DisplayLabel = $"Usuario Telegram ({telegramUserId}) [logs]"
                });
            }
        }

        if (await TableExistsAsync(connection, "tg_conversation_messages"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
            """
            SELECT phone, MAX(created_at_utc) AS last_seen
            FROM tg_conversation_messages
            WHERE tenant_id = @tenant_id
              AND phone LIKE 'tg:%'
            GROUP BY phone
            ORDER BY last_seen DESC
            LIMIT @limit;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var rawPhone = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!TryParseTelegramUserId(rawPhone, out var telegramUserId) || !knownIds.Add(telegramUserId))
                {
                    continue;
                }

                output.Add(new TelegramUserOption
                {
                    TelegramUserId = telegramUserId,
                    DisplayLabel = $"Usuario Telegram ({telegramUserId}) [legacy]"
                });
            }
        }

        if (await TableExistsAsync(connection, "tg_conversation_state"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
            """
            SELECT phone
            FROM tg_conversation_state
            WHERE tenant_id = @tenant_id
              AND phone LIKE 'tg:%'
            ORDER BY updated_at_utc DESC
            LIMIT @limit;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@limit", safeLimit);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var rawPhone = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!TryParseTelegramUserId(rawPhone, out var telegramUserId) || !knownIds.Add(telegramUserId))
                {
                    continue;
                }

                output.Add(new TelegramUserOption
                {
                    TelegramUserId = telegramUserId,
                    DisplayLabel = $"Usuario Telegram ({telegramUserId}) [legacy]"
                });
            }
        }

        return output.Take(safeLimit).ToList();
    }

    public async Task SaveBotConfigAsync(BotConfigViewModel input)
    {
        var tenant = NormalizeTenant(input.TenantId);
        var menu = new MenuConfigStorage
        {
            MainMenuText = input.MainMenuText?.Trim() ?? string.Empty
        };
        var messages = new MessagesConfigStorage
        {
            GreetingText = input.GreetingText?.Trim() ?? string.Empty,
            HumanHandoffText = input.HumanHandoffText?.Trim() ?? string.Empty,
            CloseConfirmationText = input.CloseConfirmationText?.Trim() ?? string.Empty,
            ClosingText = input.ClosingText?.Trim() ?? string.Empty,
            FallbackText = input.FallbackText?.Trim() ?? string.Empty,
            MessagePoolingSeconds = ClampPoolingSeconds(input.MessagePoolingSeconds),
            ProviderReminderEnabled = input.ProviderReminderEnabled,
            ProviderReminderSweepIntervalMinutes = ClampProviderReminderSweepIntervalMinutes(input.ProviderReminderSweepIntervalMinutes),
            ProviderReminderResendCooldownMinutes = ClampProviderReminderResendCooldownMinutes(input.ProviderReminderResendCooldownMinutes),
            ProviderReminderSnoozeHours = ClampProviderReminderSnoozeHours(input.ProviderReminderSnoozeHours)
        };
        var nowUtc = ToUtcText(DateTimeOffset.UtcNow);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var incomingToken = input.TelegramBotToken?.Trim() ?? string.Empty;
        var tokenToPersist = incomingToken;
        var incomingOpenAiApiKey = input.OpenAiApiKey?.Trim() ?? string.Empty;
        var openAiApiKeyToPersist = incomingOpenAiApiKey;
        var incomingServiceAccountJson = input.GoogleCalendarServiceAccountJson?.Trim() ?? string.Empty;
        var serviceAccountJsonToPersist = incomingServiceAccountJson;

        if (string.IsNullOrWhiteSpace(tokenToPersist))
        {
            await using var existingTokenCommand = connection.CreateCommand();
            existingTokenCommand.CommandText =
            """
            SELECT bot_token
            FROM tg_tenant_telegram_config
            WHERE tenant_id = @tenant_id
            LIMIT 1;
            """;
            existingTokenCommand.Parameters.AddWithValue("@tenant_id", tenant);
            tokenToPersist = Convert.ToString(await existingTokenCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(openAiApiKeyToPersist))
        {
            openAiApiKeyToPersist = await GetSharedSettingAsync(connection, OpenAiApiKeySettingKey);
        }

        if (string.IsNullOrWhiteSpace(serviceAccountJsonToPersist))
        {
            await using var existingGoogleJsonCommand = connection.CreateCommand();
            existingGoogleJsonCommand.CommandText =
            """
            SELECT service_account_json
            FROM tg_tenant_google_calendar_config
            WHERE tenant_id = @tenant_id
            LIMIT 1;
            """;
            existingGoogleJsonCommand.Parameters.AddWithValue("@tenant_id", tenant);
            serviceAccountJsonToPersist = Convert.ToString(await existingGoogleJsonCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var telegram = new TelegramConfigStorage
        {
            BotId = input.TelegramBotId?.Trim() ?? string.Empty,
            BotUsername = input.TelegramBotUsername?.Trim() ?? string.Empty,
            BotToken = tokenToPersist,
            IsActive = input.TelegramIsActive,
            PollingTimeoutSeconds = ClampTelegramPollingSeconds(input.TelegramPollingTimeoutSeconds),
            LastUpdateId = Math.Max(0L, input.TelegramLastUpdateId)
        };
        var googleCalendar = new GoogleCalendarConfigStorage
        {
            IsEnabled = input.GoogleCalendarEnabled,
            CalendarId = input.GoogleCalendarId?.Trim() ?? string.Empty,
            ServiceAccountJson = serviceAccountJsonToPersist,
            TimeZoneId = string.IsNullOrWhiteSpace(input.GoogleCalendarTimeZoneId)
                ? "America/Sao_Paulo"
                : input.GoogleCalendarTimeZoneId.Trim(),
            DefaultDurationMinutes = ClampGoogleCalendarDuration(input.GoogleCalendarDefaultDurationMinutes),
            AvailabilityWindowDays = ClampGoogleCalendarAvailabilityWindowDays(input.GoogleCalendarAvailabilityWindowDays),
            AvailabilitySlotIntervalMinutes = ClampGoogleCalendarAvailabilitySlotIntervalMinutes(input.GoogleCalendarAvailabilitySlotIntervalMinutes),
            AvailabilityWorkdayStartHour = ClampGoogleCalendarAvailabilityWorkdayStartHour(input.GoogleCalendarAvailabilityWorkdayStartHour),
            AvailabilityWorkdayEndHour = ClampGoogleCalendarAvailabilityWorkdayEndHour(
                input.GoogleCalendarAvailabilityWorkdayEndHour,
                ClampGoogleCalendarAvailabilityWorkdayStartHour(input.GoogleCalendarAvailabilityWorkdayStartHour)),
            AvailabilityTodayLeadMinutes = ClampGoogleCalendarAvailabilityTodayLeadMinutes(input.GoogleCalendarAvailabilityTodayLeadMinutes),
            MaxAttempts = ClampGoogleCalendarMaxAttempts(input.GoogleCalendarMaxAttempts),
            RetryBaseSeconds = ClampGoogleCalendarRetryBaseSeconds(input.GoogleCalendarRetryBaseSeconds),
            RetryMaxSeconds = ClampGoogleCalendarRetryMaxSeconds(input.GoogleCalendarRetryMaxSeconds),
            EventTitleTemplate = input.GoogleCalendarEventTitleTemplate?.Trim() ?? string.Empty,
            EventDescriptionTemplate = input.GoogleCalendarEventDescriptionTemplate?.Trim() ?? string.Empty
        };

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            INSERT INTO tg_tenant_bot_config (tenant_id, menu_json, messages_json, updated_at_utc)
            VALUES (@tenant_id, @menu_json, @messages_json, @updated_at_utc)
            ON CONFLICT(tenant_id) DO UPDATE SET
                menu_json = excluded.menu_json,
                messages_json = excluded.messages_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@menu_json", JsonSerializer.Serialize(menu));
            command.Parameters.AddWithValue("@messages_json", JsonSerializer.Serialize(messages));
            command.Parameters.AddWithValue("@updated_at_utc", nowUtc);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            INSERT INTO tg_tenant_telegram_config
            (tenant_id, bot_id, bot_username, bot_token, is_active, polling_timeout_seconds, last_update_id, updated_at_utc)
            VALUES
            (@tenant_id, @bot_id, @bot_username, @bot_token, @is_active, @polling_timeout_seconds, @last_update_id, @updated_at_utc)
            ON CONFLICT(tenant_id) DO UPDATE SET
                bot_id = excluded.bot_id,
                bot_username = excluded.bot_username,
                bot_token = excluded.bot_token,
                is_active = excluded.is_active,
                polling_timeout_seconds = excluded.polling_timeout_seconds,
                last_update_id = excluded.last_update_id,
                updated_at_utc = excluded.updated_at_utc;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@bot_id", telegram.BotId);
            command.Parameters.AddWithValue("@bot_username", telegram.BotUsername);
            command.Parameters.AddWithValue("@bot_token", telegram.BotToken);
            command.Parameters.AddWithValue("@is_active", telegram.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("@polling_timeout_seconds", telegram.PollingTimeoutSeconds);
            command.Parameters.AddWithValue("@last_update_id", telegram.LastUpdateId);
            command.Parameters.AddWithValue("@updated_at_utc", nowUtc);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            INSERT INTO tg_tenant_google_calendar_config
            (
                tenant_id,
                is_enabled,
                calendar_id,
                service_account_json,
                time_zone_id,
                default_duration_minutes,
                availability_window_days,
                availability_slot_interval_minutes,
                availability_workday_start_hour,
                availability_workday_end_hour,
                availability_today_lead_minutes,
                max_attempts,
                retry_base_seconds,
                retry_max_seconds,
                event_title_template,
                event_description_template,
                updated_at_utc
            )
            VALUES
            (
                @tenant_id,
                @is_enabled,
                @calendar_id,
                @service_account_json,
                @time_zone_id,
                @default_duration_minutes,
                @availability_window_days,
                @availability_slot_interval_minutes,
                @availability_workday_start_hour,
                @availability_workday_end_hour,
                @availability_today_lead_minutes,
                @max_attempts,
                @retry_base_seconds,
                @retry_max_seconds,
                @event_title_template,
                @event_description_template,
                @updated_at_utc
            )
            ON CONFLICT(tenant_id) DO UPDATE SET
                is_enabled = excluded.is_enabled,
                calendar_id = excluded.calendar_id,
                service_account_json = excluded.service_account_json,
                time_zone_id = excluded.time_zone_id,
                default_duration_minutes = excluded.default_duration_minutes,
                availability_window_days = excluded.availability_window_days,
                availability_slot_interval_minutes = excluded.availability_slot_interval_minutes,
                availability_workday_start_hour = excluded.availability_workday_start_hour,
                availability_workday_end_hour = excluded.availability_workday_end_hour,
                availability_today_lead_minutes = excluded.availability_today_lead_minutes,
                max_attempts = excluded.max_attempts,
                retry_base_seconds = excluded.retry_base_seconds,
                retry_max_seconds = excluded.retry_max_seconds,
                event_title_template = excluded.event_title_template,
                event_description_template = excluded.event_description_template,
                updated_at_utc = excluded.updated_at_utc;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@is_enabled", googleCalendar.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("@calendar_id", googleCalendar.CalendarId);
            command.Parameters.AddWithValue("@service_account_json", googleCalendar.ServiceAccountJson);
            command.Parameters.AddWithValue("@time_zone_id", googleCalendar.TimeZoneId);
            command.Parameters.AddWithValue("@default_duration_minutes", googleCalendar.DefaultDurationMinutes);
            command.Parameters.AddWithValue("@availability_window_days", googleCalendar.AvailabilityWindowDays);
            command.Parameters.AddWithValue("@availability_slot_interval_minutes", googleCalendar.AvailabilitySlotIntervalMinutes);
            command.Parameters.AddWithValue("@availability_workday_start_hour", googleCalendar.AvailabilityWorkdayStartHour);
            command.Parameters.AddWithValue("@availability_workday_end_hour", googleCalendar.AvailabilityWorkdayEndHour);
            command.Parameters.AddWithValue("@availability_today_lead_minutes", googleCalendar.AvailabilityTodayLeadMinutes);
            command.Parameters.AddWithValue("@max_attempts", googleCalendar.MaxAttempts);
            command.Parameters.AddWithValue("@retry_base_seconds", googleCalendar.RetryBaseSeconds);
            command.Parameters.AddWithValue("@retry_max_seconds", googleCalendar.RetryMaxSeconds);
            command.Parameters.AddWithValue("@event_title_template", googleCalendar.EventTitleTemplate);
            command.Parameters.AddWithValue("@event_description_template", googleCalendar.EventDescriptionTemplate);
            command.Parameters.AddWithValue("@updated_at_utc", nowUtc);
            await command.ExecuteNonQueryAsync();
        }

        await UpsertSharedSettingAsync(connection, OpenAiApiKeySettingKey, openAiApiKeyToPersist, nowUtc);
    }

    public async Task<TelegramMemoryResetResult> ResetTelegramMemoryAsync(string tenantId, long telegramUserId, bool clearHistory)
    {
        var tenant = NormalizeTenant(tenantId);
        var safeUserId = Math.Max(0L, telegramUserId);
        var result = new TelegramMemoryResetResult();
        if (safeUserId <= 0)
        {
            return result;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        var hasUsers = await TableExistsAsync(connection, "tg_Users", transaction);
        var hasUserSessions = await TableExistsAsync(connection, "tg_UserSessions", transaction);
        var hasMessagesLog = await TableExistsAsync(connection, "tg_MessagesLog", transaction);
        var hasLegacyMessages = await TableExistsAsync(connection, "tg_conversation_messages", transaction);
        var hasLegacyState = await TableExistsAsync(connection, "tg_conversation_state", transaction);
        var phone = safeUserId.ToString(CultureInfo.InvariantCulture);
        var prefixedPhone = $"tg:{phone}";

        if (hasUsers)
        {
            var userIds = new List<long>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                """
                SELECT Id
                FROM tg_Users
                WHERE TenantId = @tenant_id
                  AND TelegramUserId = @telegram_user_id;
                """;
                command.Parameters.AddWithValue("@tenant_id", tenant);
                command.Parameters.AddWithValue("@telegram_user_id", safeUserId);

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    userIds.Add(reader.GetInt64(0));
                }
            }

            if (userIds.Count > 0)
            {
                result.FoundUser = true;
            }

            if (hasUserSessions)
            {
                foreach (var userId in userIds)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                    """
                    UPDATE tg_UserSessions
                    SET State = 'U_ROLE_REQUIRED',
                        DraftJson = '{}',
                        ActiveJobId = NULL,
                        ChatJobId = NULL,
                        ChatPeerUserId = NULL,
                        IsChatActive = 0,
                        UpdatedAt = @updated_at
                    WHERE UserId = @user_id;
                    """;
                    command.Parameters.AddWithValue("@updated_at", ToUtcText(DateTimeOffset.UtcNow));
                    command.Parameters.AddWithValue("@user_id", userId);
                    result.SessionsReset += await command.ExecuteNonQueryAsync();
                }
            }
        }

        if (!result.FoundUser && hasMessagesLog)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT 1
            FROM tg_MessagesLog
            WHERE TenantId = @tenant_id
              AND TelegramUserId = @telegram_user_id
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@telegram_user_id", safeUserId);
            result.FoundUser = await command.ExecuteScalarAsync() is not null;
        }

        if (!result.FoundUser && hasLegacyMessages)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT 1
            FROM tg_conversation_messages
            WHERE tenant_id = @tenant_id
              AND (phone = @phone OR phone = @prefixed_phone)
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@phone", phone);
            command.Parameters.AddWithValue("@prefixed_phone", prefixedPhone);
            result.FoundUser = await command.ExecuteScalarAsync() is not null;
        }

        if (!result.FoundUser && hasLegacyState)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT 1
            FROM tg_conversation_state
            WHERE tenant_id = @tenant_id
              AND (phone = @phone OR phone = @prefixed_phone)
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@phone", phone);
            command.Parameters.AddWithValue("@prefixed_phone", prefixedPhone);
            result.FoundUser = await command.ExecuteScalarAsync() is not null;
        }

        if (clearHistory && hasMessagesLog)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            DELETE FROM tg_MessagesLog
            WHERE TenantId = @tenant_id
              AND TelegramUserId = @telegram_user_id;
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@telegram_user_id", safeUserId);
            result.TelegramMessagesDeleted = await command.ExecuteNonQueryAsync();
        }

        if (clearHistory && hasLegacyMessages)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            DELETE FROM tg_conversation_messages
            WHERE tenant_id = @tenant_id
              AND (phone = @phone OR phone = @prefixed_phone);
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@phone", phone);
            command.Parameters.AddWithValue("@prefixed_phone", prefixedPhone);
            result.LegacyConversationMessagesDeleted = await command.ExecuteNonQueryAsync();
        }

        if (hasLegacyState)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            DELETE FROM tg_conversation_state
            WHERE tenant_id = @tenant_id
              AND (phone = @phone OR phone = @prefixed_phone);
            """;
            command.Parameters.AddWithValue("@tenant_id", tenant);
            command.Parameters.AddWithValue("@phone", phone);
            command.Parameters.AddWithValue("@prefixed_phone", prefixedPhone);
            result.LegacyConversationStateDeleted = await command.ExecuteNonQueryAsync();
        }

        if (!result.FoundUser &&
            (result.SessionsReset > 0
             || result.TelegramMessagesDeleted > 0
             || result.LegacyConversationMessagesDeleted > 0
             || result.LegacyConversationStateDeleted > 0))
        {
            result.FoundUser = true;
        }

        await transaction.CommitAsync();
        return result;
    }

    public async Task<TenantOperationalResetResult> ResetTenantOperationalDataAsync(string tenantId)
    {
        var tenant = NormalizeTenant(tenantId);
        var result = new TenantOperationalResetResult();

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        var hasLegacyMessages = await TableExistsAsync(connection, "tg_conversation_messages", transaction);
        var hasLegacyState = await TableExistsAsync(connection, "tg_conversation_state", transaction);
        var hasLegacyBookings = await TableExistsAsync(connection, "tg_bookings", transaction);
        var hasLegacyGeocode = await TableExistsAsync(connection, "tg_booking_geocode_cache", transaction);

        var hasUsers = await TableExistsAsync(connection, "tg_Users", transaction);
        var hasProviderProfiles = await TableExistsAsync(connection, "tg_ProvidersProfile", transaction);
        var hasProviderPortfolio = await TableExistsAsync(connection, "tg_ProviderPortfolioPhotos", transaction);
        var hasJobs = await TableExistsAsync(connection, "tg_Jobs", transaction);
        var hasJobPhotos = await TableExistsAsync(connection, "tg_JobPhotos", transaction);
        var hasRatings = await TableExistsAsync(connection, "tg_Ratings", transaction);
        var hasMessagesLog = await TableExistsAsync(connection, "tg_MessagesLog", transaction);
        var hasUserSessions = await TableExistsAsync(connection, "tg_UserSessions", transaction);

        async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            return await command.ExecuteNonQueryAsync();
        }

        if (hasLegacyMessages)
        {
            result.LegacyConversationMessagesDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_conversation_messages
                WHERE tenant_id = @tenant_id;
                """,
                ("@tenant_id", tenant));
        }

        if (hasLegacyState)
        {
            result.LegacyConversationStateDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_conversation_state
                WHERE tenant_id = @tenant_id;
                """,
                ("@tenant_id", tenant));
        }

        if (hasLegacyGeocode)
        {
            result.LegacyBookingGeocodeCacheDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_booking_geocode_cache
                WHERE tenant_id = @tenant_id;
                """,
                ("@tenant_id", tenant));
        }

        if (hasLegacyBookings)
        {
            result.LegacyBookingsDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_bookings
                WHERE tenant_id = @tenant_id;
                """,
                ("@tenant_id", tenant));
        }

        if (hasJobPhotos && hasJobs)
        {
            result.TelegramJobPhotosDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_JobPhotos
                WHERE JobId IN (
                    SELECT Id
                    FROM tg_Jobs
                    WHERE TenantId = @tenant_id
                );
                """,
                ("@tenant_id", tenant));
        }

        if (hasRatings && hasJobs)
        {
            result.TelegramRatingsDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_Ratings
                WHERE JobId IN (
                    SELECT Id
                    FROM tg_Jobs
                    WHERE TenantId = @tenant_id
                );
                """,
                ("@tenant_id", tenant));
        }

        if (hasMessagesLog)
        {
            result.TelegramMessagesDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_MessagesLog
                WHERE TenantId = @tenant_id;
                """,
                ("@tenant_id", tenant));
        }

        if (hasJobs)
        {
            result.TelegramJobsDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_Jobs
                WHERE TenantId = @tenant_id;
                """,
                ("@tenant_id", tenant));
        }

        if (hasProviderPortfolio && hasUsers)
        {
            result.TelegramProviderPortfolioDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_ProviderPortfolioPhotos
                WHERE ProviderUserId IN (
                    SELECT Id
                    FROM tg_Users
                    WHERE TenantId = @tenant_id
                );
                """,
                ("@tenant_id", tenant));
        }

        if (hasUserSessions && hasUsers)
        {
            result.TelegramUserSessionsDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_UserSessions
                WHERE UserId IN (
                    SELECT Id
                    FROM tg_Users
                    WHERE TenantId = @tenant_id
                );
                """,
                ("@tenant_id", tenant));
        }

        if (hasProviderProfiles && hasUsers)
        {
            result.TelegramProviderProfilesDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_ProvidersProfile
                WHERE UserId IN (
                    SELECT Id
                    FROM tg_Users
                    WHERE TenantId = @tenant_id
                );
                """,
                ("@tenant_id", tenant));
        }

        if (hasUsers)
        {
            result.TelegramUsersDeleted = await ExecuteAsync(
                """
                DELETE FROM tg_Users
                WHERE TenantId = @tenant_id;
                """,
                ("@tenant_id", tenant));
        }

        await transaction.CommitAsync();
        return result;
    }

    private static string TrimPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Length <= 140 ? text : $"{text[..140]}...";
    }

    private static BotConfigViewModel BuildDefaultBotConfig(string tenant)
    {
        return new BotConfigViewModel
        {
            TenantId = tenant,
            HasOpenAiApiKey = false,
            OpenAiApiKey = string.Empty,
            MainMenuText = "1 - Agendar Servico\n2 - Consultar Agendamentos\n3 - Cancelar Agendamento\n4 - Alterar Agendamento\n5 - Falar com atendente\n6 - Encerrar atendimento",
            GreetingText = "Como posso ajudar voce hoje?",
            HumanHandoffText = "Vou te direcionar para um atendente humano.",
            CloseConfirmationText = "O atendimento sera encerrado e qualquer pedido em andamento sera descartado. Deseja continuar?",
            ClosingText = "Atendimento encerrado. Se precisar de alguma coisa, e so mandar um Oi.",
            FallbackText = "Nao entendi. Escolha uma opcao do menu.",
            MessagePoolingSeconds = 15,
            TelegramPollingTimeoutSeconds = 30,
            ProviderReminderEnabled = true,
            ProviderReminderSweepIntervalMinutes = 5,
            ProviderReminderResendCooldownMinutes = 5,
            ProviderReminderSnoozeHours = 24,
            GoogleCalendarEnabled = false,
            GoogleCalendarId = string.Empty,
            GoogleCalendarServiceAccountJson = string.Empty,
            HasGoogleCalendarServiceAccountJson = false,
            GoogleCalendarTimeZoneId = "America/Sao_Paulo",
            GoogleCalendarDefaultDurationMinutes = 60,
            GoogleCalendarAvailabilityWindowDays = 7,
            GoogleCalendarAvailabilitySlotIntervalMinutes = 60,
            GoogleCalendarAvailabilityWorkdayStartHour = 8,
            GoogleCalendarAvailabilityWorkdayEndHour = 20,
            GoogleCalendarAvailabilityTodayLeadMinutes = 30,
            GoogleCalendarMaxAttempts = 8,
            GoogleCalendarRetryBaseSeconds = 10,
            GoogleCalendarRetryMaxSeconds = 600,
            GoogleCalendarEventTitleTemplate = "Agendamento #{job_id} - {category}",
            GoogleCalendarEventDescriptionTemplate = "Cliente: {client_name}\nTelefone: {client_phone}\nCategoria: {category}\nDescricao: {description}\nStatus: {status}\nEndereco: {address}\nTenant: {tenant_id}\nJobId: {job_id}"
        };
    }

    private static MenuConfigStorage DeserializeMenu(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new MenuConfigStorage()
                : JsonSerializer.Deserialize<MenuConfigStorage>(json) ?? new MenuConfigStorage();
        }
        catch
        {
            return new MenuConfigStorage();
        }
    }

    private static MessagesConfigStorage DeserializeMessages(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new MessagesConfigStorage()
                : JsonSerializer.Deserialize<MessagesConfigStorage>(json) ?? new MessagesConfigStorage();
        }
        catch
        {
            return new MessagesConfigStorage();
        }
    }

    private static int ClampPoolingSeconds(int? value)
        => Math.Clamp(value ?? 15, 0, 120);

    private static int ClampProviderReminderSweepIntervalMinutes(int? value)
        => Math.Clamp(value ?? 5, 1, 1440);

    private static int ClampProviderReminderResendCooldownMinutes(int? value)
        => Math.Clamp(value ?? 5, 1, 1440);

    private static int ClampProviderReminderSnoozeHours(int? value)
        => Math.Clamp(value ?? 24, 1, 168);

    private static int ClampTelegramPollingSeconds(int value)
        => Math.Clamp(value, 5, 50);

    private static int ClampGoogleCalendarDuration(int value)
        => Math.Clamp(value, 15, 720);

    private static int ClampGoogleCalendarAvailabilityWindowDays(int value)
        => Math.Clamp(value, 1, 30);

    private static int ClampGoogleCalendarAvailabilitySlotIntervalMinutes(int value)
        => Math.Clamp(value, 15, 240);

    private static int ClampGoogleCalendarAvailabilityWorkdayStartHour(int value)
        => Math.Clamp(value, 0, 23);

    private static int ClampGoogleCalendarAvailabilityWorkdayEndHour(int value, int startHour)
    {
        var safeStart = ClampGoogleCalendarAvailabilityWorkdayStartHour(startHour);
        var safeEnd = Math.Clamp(value, 1, 24);
        return safeEnd <= safeStart ? Math.Min(24, safeStart + 1) : safeEnd;
    }

    private static int ClampGoogleCalendarAvailabilityTodayLeadMinutes(int value)
        => Math.Clamp(value, 0, 720);

    private static int ClampGoogleCalendarMaxAttempts(int value)
        => Math.Clamp(value, 1, 30);

    private static int ClampGoogleCalendarRetryBaseSeconds(int value)
        => Math.Clamp(value, 5, 600);

    private static int ClampGoogleCalendarRetryMaxSeconds(int value)
        => Math.Clamp(value, 10, 86400);

    private static async Task<GeocodeResult> TryGeocodeAddressAsync(string address)
    {
        var safeAddress = (address ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(safeAddress))
        {
            return GeocodeResult.Fail("Endereco vazio.");
        }

        var candidates = BuildAddressGeocodeCandidates(safeAddress).ToList();
        if (TryNormalizeCepOnly(safeAddress, out var cep))
        {
            var awesomeByCep = await TryGeocodeByAwesomeCepAsync(cep);
            if (awesomeByCep.Success)
            {
                return awesomeByCep;
            }

            var viaCepAddress = await TryResolveAddressByCepAsync(cep);
            if (!string.IsNullOrWhiteSpace(viaCepAddress))
            {
                candidates.Insert(0, viaCepAddress);
            }
        }

        string? lastError = null;
        foreach (var candidate in candidates)
        {
            var result = await TryQueryNominatimAsync(candidate);
            if (result.Success)
            {
                return result;
            }

            lastError = result.ErrorMessage;
        }

        if (TryExtractCepFromText(safeAddress, out var cepFromText))
        {
            var awesomeFallback = await TryGeocodeByAwesomeCepAsync(cepFromText);
            if (awesomeFallback.Success)
            {
                return awesomeFallback;
            }
        }

        return GeocodeResult.Fail(lastError ?? "Nenhum resultado.");
    }

    private static async Task<GeocodeResult> TryQueryNominatimAsync(string queryAddress)
    {
        var query = Uri.EscapeDataString($"{queryAddress}, Brasil");
        var endpoint = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=br&q={query}";

        try
        {
            using var response = await GeocodeHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return GeocodeResult.Fail($"HTTP {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return GeocodeResult.Fail("Nenhum resultado.");
            }

            var first = document.RootElement[0];
            var latText = first.TryGetProperty("lat", out var latProp) ? latProp.GetString() : null;
            var lonText = first.TryGetProperty("lon", out var lonProp) ? lonProp.GetString() : null;

            if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                return GeocodeResult.Fail("Lat/lon invalidos.");
            }

            return GeocodeResult.Ok(lat, lon);
        }
        catch (Exception ex)
        {
            return GeocodeResult.Fail(ex.Message);
        }
    }

    private static IReadOnlyList<string> BuildAddressGeocodeCandidates(string rawAddress)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string NormalizeCandidate(string value)
        {
            var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s*,\s*", ", ");
            normalized = Regex.Replace(normalized, @",\s*,+", ", ");
            return normalized.Trim().Trim(',');
        }

        void Add(string value)
        {
            var normalized = NormalizeCandidate(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (seen.Add(normalized))
            {
                output.Add(normalized);
            }
        }

        Add(rawAddress);

        var withoutComplement = Regex.Replace(
            rawAddress,
            @"(?i),?\s*(apto|apt|apartamento|bloco|casa|fundos|sala|conjunto|complemento)\b[^,]*",
            string.Empty);
        Add(withoutComplement);

        var withoutNumber = Regex.Replace(
            withoutComplement,
            @"(?<!\d)\d{1,6}[A-Za-z]?(?!\d)",
            string.Empty);
        Add(withoutNumber);

        Add(withoutNumber.Replace(" - ", ", ", StringComparison.Ordinal));

        var parts = withoutNumber
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length >= 3)
        {
            Add(string.Join(", ", parts.TakeLast(3)));
        }

        if (parts.Length >= 2)
        {
            Add(string.Join(", ", parts.TakeLast(2)));
        }

        return output;
    }

    private static bool TryNormalizeCepOnly(string rawAddress, out string cep)
    {
        cep = string.Empty;
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return false;
        }

        var safe = rawAddress.Trim();
        foreach (var ch in safe)
        {
            if (!char.IsDigit(ch) && ch != '-' && !char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        var digits = new string(safe.Where(char.IsDigit).ToArray());
        if (digits.Length != 8)
        {
            return false;
        }

        cep = digits;
        return true;
    }

    private static bool TryExtractCepFromText(string rawAddress, out string cep)
    {
        cep = string.Empty;
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return false;
        }

        var match = Regex.Match(rawAddress, @"\b\d{5}-?\d{3}\b");
        if (!match.Success)
        {
            return false;
        }

        var digits = new string(match.Value.Where(char.IsDigit).ToArray());
        if (digits.Length != 8)
        {
            return false;
        }

        cep = digits;
        return true;
    }

    private static async Task<GeocodeResult> TryGeocodeByAwesomeCepAsync(string cep)
    {
        if (string.IsNullOrWhiteSpace(cep) || cep.Length != 8)
        {
            return GeocodeResult.Fail("CEP invalido para AwesomeAPI.");
        }

        try
        {
            using var response = await GeocodeHttpClient.GetAsync($"https://cep.awesomeapi.com.br/json/{cep}");
            if (!response.IsSuccessStatusCode)
            {
                return GeocodeResult.Fail($"AwesomeAPI HTTP {(int)response.StatusCode}");
            }

            var payloadText = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payloadText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return GeocodeResult.Fail("AwesomeAPI payload invalido.");
            }

            var root = document.RootElement;
            var latText = root.TryGetProperty("lat", out var latProp) ? latProp.GetString() : null;
            var lngText = root.TryGetProperty("lng", out var lngProp) ? lngProp.GetString() : null;

            if (string.IsNullOrWhiteSpace(latText) || string.IsNullOrWhiteSpace(lngText))
            {
                return GeocodeResult.Fail("AwesomeAPI sem lat/lng.");
            }

            if (!double.TryParse(
                    latText.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var lat)
                || !double.TryParse(
                    lngText.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var lng))
            {
                return GeocodeResult.Fail("AwesomeAPI lat/lng invalidos.");
            }

            return GeocodeResult.Ok(lat, lng);
        }
        catch (Exception ex)
        {
            return GeocodeResult.Fail(ex.Message);
        }
    }

    private static async Task<string?> TryResolveAddressByCepAsync(string cep)
    {
        if (string.IsNullOrWhiteSpace(cep))
        {
            return null;
        }

        try
        {
            using var response = await GeocodeHttpClient.GetAsync($"https://viacep.com.br/ws/{cep}/json/");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("erro", out var erroProperty)
                && erroProperty.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            var logradouro = root.TryGetProperty("logradouro", out var logradouroProp)
                ? (logradouroProp.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var bairro = root.TryGetProperty("bairro", out var bairroProp)
                ? (bairroProp.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var localidade = root.TryGetProperty("localidade", out var localidadeProp)
                ? (localidadeProp.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var uf = root.TryGetProperty("uf", out var ufProp)
                ? (ufProp.GetString() ?? string.Empty).Trim().ToUpperInvariant()
                : string.Empty;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(logradouro))
            {
                parts.Add(logradouro);
            }

            if (!string.IsNullOrWhiteSpace(bairro))
            {
                parts.Add(bairro);
            }

            if (!string.IsNullOrWhiteSpace(localidade) && !string.IsNullOrWhiteSpace(uf))
            {
                parts.Add($"{localidade} - {uf}");
            }
            else if (!string.IsNullOrWhiteSpace(localidade))
            {
                parts.Add(localidade);
            }
            else if (!string.IsNullOrWhiteSpace(uf))
            {
                parts.Add(uf);
            }

            if (parts.Count == 0)
            {
                return null;
            }

            return string.Join(", ", parts);
        }
        catch
        {
            return null;
        }
    }

    private static async Task UpsertGeocodeCacheAsync(SqliteConnection connection, DashboardMapRow row, string? errorMessage)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tg_booking_geocode_cache
            (booking_id, tenant_id, address, latitude, longitude, status, error_message, geocoded_at_utc, retry_after_utc)
            VALUES
            (@booking_id, @tenant_id, @address, @latitude, @longitude, @status, @error_message, @geocoded_at_utc, @retry_after_utc)
            ON CONFLICT(booking_id) DO UPDATE SET
                tenant_id = excluded.tenant_id,
                address = excluded.address,
                latitude = excluded.latitude,
                longitude = excluded.longitude,
                status = excluded.status,
                error_message = excluded.error_message,
                geocoded_at_utc = excluded.geocoded_at_utc,
                retry_after_utc = excluded.retry_after_utc;
            """;
        command.Parameters.AddWithValue("@booking_id", row.BookingId);
        command.Parameters.AddWithValue("@tenant_id", row.TenantId);
        command.Parameters.AddWithValue("@address", row.Address);
        command.Parameters.AddWithValue("@latitude", row.Latitude.HasValue ? row.Latitude.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("@longitude", row.Longitude.HasValue ? row.Longitude.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(row.GeocodeStatus) ? "failed" : row.GeocodeStatus);
        command.Parameters.AddWithValue("@error_message", string.IsNullOrWhiteSpace(errorMessage) ? (object)DBNull.Value : errorMessage);
        command.Parameters.AddWithValue("@geocoded_at_utc", ToUtcText(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@retry_after_utc", row.RetryAfterUtc.HasValue ? ToUtcText(row.RetryAfterUtc.Value) : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(double? Latitude, double? Longitude, string Status, DateTimeOffset? RetryAfterUtc)> TryGetGeocodeCacheAsync(
        SqliteConnection connection,
        string bookingId,
        string tenantId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT latitude, longitude, status, retry_after_utc
            FROM tg_booking_geocode_cache
            WHERE booking_id = @booking_id
              AND tenant_id = @tenant_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@booking_id", bookingId);
        command.Parameters.AddWithValue("@tenant_id", tenantId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (null, null, string.Empty, null);
        }

        double? latitude = reader.IsDBNull(0) ? null : reader.GetDouble(0);
        double? longitude = reader.IsDBNull(1) ? null : reader.GetDouble(1);
        var status = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var retryAfterUtc = TryParseNullableUtc(reader.IsDBNull(3) ? null : reader.GetString(3));
        return (latitude, longitude, status, retryAfterUtc);
    }

    private static async Task UpdateJobCoordinatesAsync(
        SqliteConnection connection,
        string tenantId,
        long jobId,
        double latitude,
        double longitude)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tg_Jobs
            SET Latitude = @latitude,
                Longitude = @longitude
            WHERE Id = @job_id
              AND TenantId = @tenant_id
              AND (Latitude IS NULL OR Longitude IS NULL);
            """;
        command.Parameters.AddWithValue("@job_id", jobId);
        command.Parameters.AddWithValue("@tenant_id", tenantId);
        command.Parameters.AddWithValue("@latitude", latitude);
        command.Parameters.AddWithValue("@longitude", longitude);
        await command.ExecuteNonQueryAsync();
    }

    private static bool TryParseJobBookingId(string bookingId, out long jobId)
    {
        jobId = 0;
        if (string.IsNullOrWhiteSpace(bookingId)
            || !bookingId.StartsWith("job:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(
            bookingId[4..],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out jobId)
            && jobId > 0;
    }

    private static bool TryExtractCoordinatesFromText(string? text, out double latitude, out double longitude)
    {
        latitude = 0d;
        longitude = 0d;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var safe = text.Trim();
        var latMatch = Regex.Match(
            safe,
            @"(?i)\b(?:lat|latitude)\s*[:=]\s*(-?\d{1,2}(?:[.,]\d+)?)");
        var lngMatch = Regex.Match(
            safe,
            @"(?i)\b(?:lng|lon|longitude)\s*[:=]\s*(-?\d{1,3}(?:[.,]\d+)?)");

        if (!latMatch.Success || !lngMatch.Success)
        {
            return false;
        }

        var latText = latMatch.Groups[1].Value.Replace(',', '.');
        var lngText = lngMatch.Groups[1].Value.Replace(',', '.');

        if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)
            || !double.TryParse(lngText, NumberStyles.Float, CultureInfo.InvariantCulture, out longitude))
        {
            return false;
        }

        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
    }

    private static string ParseCategoriesSummary(string? categoriesJson)
    {
        if (string.IsNullOrWhiteSpace(categoriesJson))
        {
            return "-";
        }

        try
        {
            using var doc = JsonDocument.Parse(categoriesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return "-";
            }

            var categories = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = item.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                categories.Add(value);
            }

            if (categories.Count == 0)
            {
                return "-";
            }

            var joined = string.Join(", ", categories.Take(4));
            return categories.Count > 4 ? $"{joined}..." : joined;
        }
        catch
        {
            return "-";
        }
    }

    private static string ExtractNeighborhoodFromAddress(string? rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(rawAddress, @"\s+", " ").Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var tokens = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 3)
        {
            var candidate = CleanupNeighborhoodToken(tokens[2]);
            if (IsLikelyNeighborhoodToken(candidate))
            {
                return ToTitleCase(candidate);
            }
        }

        for (var i = 2; i < tokens.Length; i++)
        {
            var candidate = CleanupNeighborhoodToken(tokens[i]);
            if (IsLikelyNeighborhoodToken(candidate))
            {
                return ToTitleCase(candidate);
            }
        }

        return string.Empty;
    }

    private static string CleanupNeighborhoodToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var value = token.Trim();
        value = Regex.Replace(value, @"^bairro\s+", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bCEP\b.*$", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\s*-\s*[A-Z]{2}\b.*$", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\s+", " ").Trim(' ', '-', '.', ';', ':');
        return value;
    }

    private static bool IsLikelyNeighborhoodToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.Length is < 2 or > 80)
        {
            return false;
        }

        if (Regex.IsMatch(token, @"^\d+$"))
        {
            return false;
        }

        return !Regex.IsMatch(
            token,
            @"\b(?:rua|r\.|avenida|av\.|travessa|alameda|estrada|rodovia|numero|nº)\b",
            RegexOptions.IgnoreCase);
    }

    private static bool IsWithinCoverageRadius(
        double originLatitude,
        double originLongitude,
        int radiusKm,
        double candidateLatitude,
        double candidateLongitude)
    {
        var safeRadiusKm = Math.Max(1d, radiusKm);
        var distanceKm = HaversineDistanceKm(originLatitude, originLongitude, candidateLatitude, candidateLongitude);
        return distanceKm <= safeRadiusKm;
    }

    private static double HaversineDistanceKm(
        double originLatitude,
        double originLongitude,
        double candidateLatitude,
        double candidateLongitude)
    {
        const double EarthRadiusKm = 6371.0088d;

        static double ToRadians(double value) => Math.PI * value / 180d;

        var dLat = ToRadians(candidateLatitude - originLatitude);
        var dLon = ToRadians(candidateLongitude - originLongitude);

        var lat1 = ToRadians(originLatitude);
        var lat2 = ToRadians(candidateLatitude);

        var a = Math.Pow(Math.Sin(dLat / 2d), 2d)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2d), 2d);
        var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return EarthRadiusKm * c;
    }

    private static async Task<string> TryReverseGeocodeNeighborhoodAsync(double latitude, double longitude)
    {
        var cacheKey = $"{Math.Round(latitude, 4).ToString("0.0000", CultureInfo.InvariantCulture)},{Math.Round(longitude, 4).ToString("0.0000", CultureInfo.InvariantCulture)}";

        if (ReverseNeighborhoodCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var latText = latitude.ToString("0.000000", CultureInfo.InvariantCulture);
        var lonText = longitude.ToString("0.000000", CultureInfo.InvariantCulture);
        var endpoint =
            $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&addressdetails=1&zoom=17&lat={latText}&lon={lonText}";

        try
        {
            using var response = await GeocodeHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                ReverseNeighborhoodCache.TryAdd(cacheKey, string.Empty);
                return string.Empty;
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("address", out var address) ||
                address.ValueKind != JsonValueKind.Object)
            {
                ReverseNeighborhoodCache.TryAdd(cacheKey, string.Empty);
                return string.Empty;
            }

            string? rawNeighborhood = null;
            foreach (var key in new[] { "neighbourhood", "suburb", "quarter", "city_district", "borough", "town", "village" })
            {
                if (address.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    rawNeighborhood = value.GetString();
                    if (!string.IsNullOrWhiteSpace(rawNeighborhood))
                    {
                        break;
                    }
                }
            }

            var cleaned = CleanupNeighborhoodToken(rawNeighborhood ?? string.Empty);
            if (!IsLikelyNeighborhoodToken(cleaned))
            {
                cleaned = string.Empty;
            }
            else
            {
                cleaned = ToTitleCase(cleaned);
            }

            ReverseNeighborhoodCache.TryAdd(cacheKey, cleaned);
            return cleaned;
        }
        catch
        {
            ReverseNeighborhoodCache.TryAdd(cacheKey, string.Empty);
            return string.Empty;
        }
    }

    private static DateTimeOffset? TryParseNullableUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static HttpClient BuildGeocodeHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BotAgendamentoAI.Admin/1.0 (+dashboard-map)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static string BuildSafeCategoryName(string rawName)
    {
        var trimmed = (rawName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Nome de categoria e obrigatorio.");
        }

        var normalized = NormalizeCategoryKey(trimmed);
        if (IsDisallowedCategory(normalized))
        {
            throw new InvalidOperationException("Categoria invalida. Nao use 'Outros'.");
        }

        return ToTitleCase(trimmed);
    }

    private static string BuildSafeServiceTitle(string rawTitle)
    {
        var trimmed = (rawTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Titulo do servico e obrigatorio.");
        }

        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }

    private static bool IsDisallowedCategory(string normalizedKey)
    {
        return normalizedKey is
            "outro" or
            "outros" or
            "outra" or
            "outras" or
            "geral" or
            "generico" or
            "diversos" or
            "diverso";
    }

    private static string NormalizeCategoryKey(string value)
    {
        var lowered = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lowered.Length);
        foreach (var c in lowered)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : ' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string ToTitleCase(string input)
    {
        var normalized = Regex.Replace(input.Trim(), @"\s+", " ");
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var token = words[i];
            words[i] = token.Length == 1
                ? token.ToUpperInvariant()
                : char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
        }

        return string.Join(' ', words);
    }

    private static async Task<CategoryItem?> FindCategoryByNormalizedAsync(SqliteConnection connection, string tenantId, string normalized)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, name, normalized_name, created_at_utc
            FROM tg_service_categories
            WHERE tenant_id = @tenant_id AND normalized_name = @normalized_name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenantId);
        command.Parameters.AddWithValue("@normalized_name", normalized);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new CategoryItem
        {
            Id = reader.GetInt64(0),
            TenantId = reader.GetString(1),
            Name = reader.GetString(2),
            NormalizedName = reader.IsDBNull(3) ? normalized : reader.GetString(3),
            CreatedAtUtc = ParseUtc(reader.GetString(4))
        };
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT 1
        FROM sqlite_master
        WHERE type = 'table'
          AND name = @table_name
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("@table_name", tableName);
        var scalar = await command.ExecuteScalarAsync();
        return scalar is not null && scalar is not DBNull;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
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
        var scalar = await command.ExecuteScalarAsync();
        return scalar is not null && scalar is not DBNull;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinitionSql)
    {
        if (!await TableExistsAsync(connection, tableName))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinitionSql};";
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists.
        }
    }

    private static async Task<int> QueryIntAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var scalar = await command.ExecuteScalarAsync();
        return scalar is null || scalar is DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<int> GetDefaultJobDurationMinutesAsync(SqliteConnection connection, string tenantId)
    {
        if (!await TableExistsAsync(connection, "tg_tenant_google_calendar_config"))
        {
            return 60;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT default_duration_minutes
            FROM tg_tenant_google_calendar_config
            WHERE tenant_id = @tenant_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenantId);
        var scalar = await command.ExecuteScalarAsync();
        if (scalar is null or DBNull)
        {
            return 60;
        }

        return Math.Clamp(
            Convert.ToInt32(scalar, CultureInfo.InvariantCulture),
            15,
            720);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReadBool(SqliteDataReader reader, int ordinal, bool defaultValue = false)
    {
        if (reader.IsDBNull(ordinal))
        {
            return defaultValue;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool boolValue => boolValue,
            byte byteValue => byteValue != 0,
            short shortValue => shortValue != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            string text when bool.TryParse(text, out var parsedBool) => parsedBool,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt) => parsedInt != 0,
            IConvertible convertible => convertible.ToInt32(CultureInfo.InvariantCulture) != 0,
            _ => defaultValue
        };
    }

    private static async Task<string> GetSharedSettingAsync(SqliteConnection connection, string settingKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT setting_value
            FROM tg_shared_settings
            WHERE setting_key = @setting_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@setting_key", settingKey);
        var scalar = await command.ExecuteScalarAsync();
        return Convert.ToString(scalar, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static async Task UpsertSharedSettingAsync(
        SqliteConnection connection,
        string settingKey,
        string settingValue,
        string updatedAtUtc)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tg_shared_settings (setting_key, setting_value, updated_at_utc)
            VALUES (@setting_key, @setting_value, @updated_at_utc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("@setting_key", settingKey);
        command.Parameters.AddWithValue("@setting_value", (settingValue ?? string.Empty).Trim());
        command.Parameters.AddWithValue("@updated_at_utc", updatedAtUtc);
        await command.ExecuteNonQueryAsync();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static ConversationHandoffStatus BuildUnavailableHandoffStatus(string tenantId, string phone)
    {
        return new ConversationHandoffStatus
        {
            TenantId = tenantId,
            Phone = phone,
            IsTelegramThread = false,
            IsOpen = false,
            RequestedByRole = string.Empty,
            AssignedAgent = string.Empty,
            PreviousState = string.Empty,
            CloseReason = string.Empty,
            RequestedAtUtc = null,
            AcceptedAtUtc = null,
            ClosedAtUtc = null,
            LastMessageAtUtc = null
        };
    }

    private static string ResolveTelegramRole(string direction, string messageType)
    {
        if (string.Equals(direction, "Out", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(messageType, "Event", StringComparison.OrdinalIgnoreCase)
                ? "human"
                : "assistant";
        }

        return "user";
    }

    private static bool IsInboundConversationDirection(string? direction)
        => string.Equals(direction?.Trim(), "In", StringComparison.OrdinalIgnoreCase);

    private static string ResolveHomeStateFromRole(string? role)
    {
        return string.Equals(role?.Trim(), "Provider", StringComparison.OrdinalIgnoreCase)
            ? "P_HOME"
            : "C_HOME";
    }

    private static Task<TelegramSendResult> SendTelegramTextAsync(string token, long chatId, string text)
        => SendTelegramTextAsync(token, chatId, text, null);

    private static async Task<TelegramSendResult> SendTelegramTextAsync(string token, long chatId, string text, object? replyMarkup)
    {
        try
        {
            var requestPayload = JsonSerializer.Serialize(new
            {
                chat_id = chatId,
                text,
                reply_markup = replyMarkup
            });
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.telegram.org/bot{token.Trim()}/sendMessage")
            {
                Content = new StringContent(requestPayload, Encoding.UTF8, "application/json")
            };

            using var response = await TelegramHttpClient.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return TelegramSendResult.Fail($"Falha ao enviar mensagem ao Telegram (HTTP {(int)response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement)
                     && okElement.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var description = root.TryGetProperty("description", out var descriptionElement)
                    ? descriptionElement.GetString()
                    : null;
                return TelegramSendResult.Fail(
                    string.IsNullOrWhiteSpace(description)
                        ? "Telegram recusou o envio da mensagem."
                        : description.Trim());
            }

            long? messageId = null;
            if (root.TryGetProperty("result", out var resultElement)
                && resultElement.ValueKind == JsonValueKind.Object
                && resultElement.TryGetProperty("message_id", out var messageIdElement))
            {
                if (messageIdElement.ValueKind == JsonValueKind.Number && messageIdElement.TryGetInt64(out var parsedId))
                {
                    messageId = parsedId;
                }
            }

            return TelegramSendResult.Ok(messageId);
        }
        catch (Exception ex)
        {
            return TelegramSendResult.Fail($"Erro ao enviar mensagem ao Telegram: {ex.Message}");
        }
    }

    private static string NormalizeCepDigits(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 8 ? digits : string.Empty;
    }

    private static string BuildAssistedOrderDraftJson(ConversationOrderDraftCommand input)
    {
        var payload = new
        {
            Category = input.Category,
            Description = input.Description,
            PhotoFileIds = Array.Empty<string>(),
            AddressText = input.AddressText,
            Cep = input.Cep,
            AddressBaseFromCep = string.Empty,
            WaitingAddressNumber = false,
            WaitingAddressConfirmation = false,
            Latitude = input.Latitude,
            Longitude = input.Longitude,
            IsUrgent = input.IsUrgent,
            ScheduledAt = input.ScheduledAt,
            PreferenceCode = input.PreferenceCode,
            ContactName = input.ContactName,
            ContactPhone = input.ContactPhone,
            FinalAmount = (decimal?)null,
            FinalNotes = (string?)null,
            AfterPhotoFileIds = Array.Empty<string>(),
            HiddenFeedJobIds = Array.Empty<long>(),
            ProviderCategoryNames = Array.Empty<string>(),
            ProviderProfileReminderLastSentUtc = (DateTimeOffset?)null,
            ProviderProfileReminderSnoozeUntilUtc = (DateTimeOffset?)null
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildAssistedOrderConfirmationText(ConversationOrderDraftCommand input)
    {
        return string.Join(
            "\n\n",
            new[]
            {
                "O atendente preparou seu pedido. Revise os dados abaixo e toque em Confirmar.",
                "7/7 - Confirme seu pedido:",
                BuildAssistedOrderSummary(input)
            });
    }

    private static string BuildAssistedOrderSummary(ConversationOrderDraftCommand input)
    {
        var lines = new List<string>
        {
            $"Categoria: {input.Category}",
            $"Descricao: {input.Description}",
            string.IsNullOrWhiteSpace(input.ContactName) ? string.Empty : $"Contato: {input.ContactName}",
            string.IsNullOrWhiteSpace(input.ContactPhone) ? string.Empty : $"Telefone contato: {input.ContactPhone}",
            $"Endereco: {input.AddressText}",
            $"Quando: {(input.IsUrgent ? "Urgente" : input.ScheduledAt?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "Hoje")}",
            $"Preferencia: {ResolvePreferenceLabel(input.PreferenceCode)}"
        };

        return string.Join("\n", lines.Where(static line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string ResolvePreferenceLabel(string? code)
    {
        return (code ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "LOW" => "Menor preco",
            "RAT" => "Melhor avaliados",
            "FAST" => "Mais rapido",
            "CHO" => "Escolher prestador",
            _ => string.IsNullOrWhiteSpace(code) ? "Melhor avaliados" : code.Trim()
        };
    }

    private static async Task<IReadOnlyList<BookingStatusHistoryItem>> GetJobStatusHistoryAsync(
        SqliteConnection connection,
        string tenantId,
        long jobId,
        string currentStatus,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        var items = new List<BookingStatusHistoryItem>();
        var initialStatus = string.Equals(currentStatus, "Requested", StringComparison.OrdinalIgnoreCase)
            ? "Requested"
            : "WaitingProvider";

        AddBookingStatusHistoryItem(
            items,
            initialStatus,
            "Pedido criado e enviado para distribuicao.",
            createdAtUtc);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              COALESCE(Text, ''),
              CreatedAt
            FROM tg_MessagesLog
            WHERE TenantId = @tenant_id
              AND RelatedJobId = @job_id
            ORDER BY CreatedAt ASC, Id ASC;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenantId);
        command.Parameters.AddWithValue("@job_id", jobId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var content = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var atUtc = ParseUtc(reader.IsDBNull(1) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(1));
            if (!TryMapBookingTimelineEvent(content, out var status, out var description))
            {
                continue;
            }

            AddBookingStatusHistoryItem(items, status, description, atUtc);
        }

        EnsureCurrentBookingStatusInHistory(items, currentStatus, updatedAtUtc);
        return items
            .OrderByDescending(x => x.AtUtc)
            .ToList();
    }

    private static bool TryMapBookingTimelineEvent(string? content, out string status, out string description)
    {
        status = string.Empty;
        description = string.Empty;

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var text = content.Trim();
        if (text.StartsWith("Novo pedido #", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Pedido criado!", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Acompanhe seu pedido", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.StartsWith("Voce aceitou o pedido #", StringComparison.OrdinalIgnoreCase)
            || text.Contains("foi aceito por", StringComparison.OrdinalIgnoreCase))
        {
            status = "Accepted";
            description = text;
            return true;
        }

        if (text.StartsWith("Status atualizado:", StringComparison.OrdinalIgnoreCase))
        {
            var token = text[(text.IndexOf(':') + 1)..];
            status = NormalizeBookingStatusToken(token);
            description = text;
            return !string.IsNullOrWhiteSpace(status);
        }

        if (text.StartsWith("Atualizacao do pedido #", StringComparison.OrdinalIgnoreCase))
        {
            var colonIndex = text.LastIndexOf(':');
            if (colonIndex >= 0 && colonIndex < text.Length - 1)
            {
                status = NormalizeBookingStatusToken(text[(colonIndex + 1)..]);
                description = text;
                return !string.IsNullOrWhiteSpace(status);
            }
        }

        if (text.Contains("foi finalizado", StringComparison.OrdinalIgnoreCase)
            || (text.StartsWith("Pedido #", StringComparison.OrdinalIgnoreCase)
                && text.Contains("finalizado", StringComparison.OrdinalIgnoreCase)))
        {
            status = "Finished";
            description = text;
            return true;
        }

        if (text.Contains("cancelado com sucesso", StringComparison.OrdinalIgnoreCase))
        {
            status = "Cancelled";
            description = text;
            return true;
        }

        if (text.Contains("reagendado para", StringComparison.OrdinalIgnoreCase))
        {
            status = "Rescheduled";
            description = text;
            return true;
        }

        return false;
    }

    private static void EnsureCurrentBookingStatusInHistory(
        List<BookingStatusHistoryItem> items,
        string currentStatus,
        DateTimeOffset updatedAtUtc)
    {
        var normalizedStatus = NormalizeBookingStatusToken(currentStatus);
        if (string.IsNullOrWhiteSpace(normalizedStatus))
        {
            return;
        }

        if (items.Any(x => string.Equals(
                NormalizeBookingStatusToken(x.Status),
                normalizedStatus,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        AddBookingStatusHistoryItem(
            items,
            normalizedStatus,
            $"Status atual: {FormatBookingStatusLabel(normalizedStatus)}.",
            updatedAtUtc);
    }

    private static void AddBookingStatusHistoryItem(
        List<BookingStatusHistoryItem> items,
        string status,
        string description,
        DateTimeOffset atUtc)
    {
        var normalizedStatus = NormalizeBookingStatusToken(status);
        if (string.IsNullOrWhiteSpace(normalizedStatus))
        {
            return;
        }

        if (items.Any(x =>
                string.Equals(NormalizeBookingStatusToken(x.Status), normalizedStatus, StringComparison.OrdinalIgnoreCase)
                && Math.Abs((x.AtUtc - atUtc).TotalSeconds) < 120))
        {
            return;
        }

        items.Add(new BookingStatusHistoryItem
        {
            Status = normalizedStatus,
            StatusLabel = FormatBookingStatusLabel(normalizedStatus),
            Description = string.IsNullOrWhiteSpace(description)
                ? FormatBookingStatusLabel(normalizedStatus)
                : description,
            AtUtc = atUtc
        });
    }

    private static string NormalizeBookingStatusToken(string? rawValue)
    {
        var token = (rawValue ?? string.Empty)
            .Trim()
            .TrimEnd('.', '!', '?')
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        return token switch
        {
            "" => string.Empty,
            "REQUESTED" or "SOLICITADO" => "Requested",
            "WAITINGPROVIDER" or "AGUARDANDOPRESTADOR" => "WaitingProvider",
            "ACCEPTED" or "ACEITO" => "Accepted",
            "ONTHEWAY" or "ACAMINHO" => "OnTheWay",
            "ARRIVED" or "PRESTADORCHEGOU" or "CHEGOU" => "Arrived",
            "INPROGRESS" or "EMANDAMENTO" => "InProgress",
            "FINISHED" or "FINALIZADO" => "Finished",
            "CANCELLED" or "CANCELADO" => "Cancelled",
            "RESCHEDULED" or "REAGENDADO" => "Rescheduled",
            "LEGACY" => "Legacy",
            _ => rawValue?.Trim() ?? string.Empty
        };
    }

    private static string FormatBookingStatusLabel(string? status)
    {
        return NormalizeBookingStatusToken(status).ToUpperInvariant() switch
        {
            "REQUESTED" => "Solicitado",
            "WAITINGPROVIDER" => "Aguardando prestador",
            "ACCEPTED" => "Aceito",
            "ONTHEWAY" => "A caminho",
            "ARRIVED" => "Prestador chegou",
            "INPROGRESS" => "Em andamento",
            "FINISHED" => "Finalizado",
            "CANCELLED" => "Cancelado",
            "RESCHEDULED" => "Reagendado",
            "LEGACY" => "Legacy",
            _ => string.IsNullOrWhiteSpace(status) ? "Sem status" : status.Trim()
        };
    }

    private static object BuildClientOrderConfirmationKeyboard()
    {
        return new
        {
            inline_keyboard = new object[]
            {
                new object[]
                {
                    new
                    {
                        text = "Confirmar",
                        callback_data = "C:CONF:OK"
                    },
                    new
                    {
                        text = "Editar",
                        callback_data = "C:CONF:EDIT"
                    }
                },
                new object[]
                {
                    new
                    {
                        text = "Cancelar",
                        callback_data = "C:CONF:CANCEL"
                    }
                }
            }
        };
    }

    private static DateTime ParseLocalDateTime(string value)
    {
        if (DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exact))
        {
            return exact;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static DateTime ParseLocalOrUtcDateTime(string value, DateTimeOffset fallbackUtc)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallbackUtc.ToLocalTime().DateTime;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var offsetValue))
        {
            return offsetValue.ToLocalTime().DateTime;
        }

        var parsedLocal = ParseLocalDateTime(value);
        if (parsedLocal != DateTime.MinValue)
        {
            return parsedLocal;
        }

        return fallbackUtc.ToLocalTime().DateTime;
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private static string ToUtcText(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string NormalizeTenant(string tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

    private static HttpClient BuildTelegramHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

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

    private static bool TryParseTelegramUserId(string? rawValue, out long telegramUserId)
    {
        telegramUserId = 0L;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var value = rawValue.Trim();
        if (value.StartsWith("tg:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[3..].Trim();
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out telegramUserId)
               && telegramUserId > 0;
    }

    private const string OpenAiApiKeySettingKey = "openai_api_key";

    private sealed class MenuConfigStorage
    {
        public string MainMenuText { get; set; } = string.Empty;
    }

    private sealed class MessagesConfigStorage
    {
        public string GreetingText { get; set; } = string.Empty;
        public string HumanHandoffText { get; set; } = string.Empty;
        public string CloseConfirmationText { get; set; } = string.Empty;
        public string ClosingText { get; set; } = string.Empty;
        public string FallbackText { get; set; } = string.Empty;
        public int? MessagePoolingSeconds { get; set; }
        public bool? ProviderReminderEnabled { get; set; }
        public int? ProviderReminderSweepIntervalMinutes { get; set; }
        public int? ProviderReminderResendCooldownMinutes { get; set; }
        public int? ProviderReminderSnoozeHours { get; set; }
    }

    private sealed class TelegramConfigStorage
    {
        public string BotId { get; set; } = string.Empty;
        public string BotUsername { get; set; } = string.Empty;
        public string BotToken { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int PollingTimeoutSeconds { get; set; }
        public long LastUpdateId { get; set; }
    }

    private sealed class GoogleCalendarConfigStorage
    {
        public bool IsEnabled { get; set; }
        public string CalendarId { get; set; } = string.Empty;
        public string ServiceAccountJson { get; set; } = string.Empty;
        public string TimeZoneId { get; set; } = "America/Sao_Paulo";
        public int DefaultDurationMinutes { get; set; } = 60;
        public int AvailabilityWindowDays { get; set; } = 7;
        public int AvailabilitySlotIntervalMinutes { get; set; } = 60;
        public int AvailabilityWorkdayStartHour { get; set; } = 8;
        public int AvailabilityWorkdayEndHour { get; set; } = 20;
        public int AvailabilityTodayLeadMinutes { get; set; } = 30;
        public int MaxAttempts { get; set; } = 8;
        public int RetryBaseSeconds { get; set; } = 10;
        public int RetryMaxSeconds { get; set; } = 600;
        public string EventTitleTemplate { get; set; } = string.Empty;
        public string EventDescriptionTemplate { get; set; } = string.Empty;
    }

    private sealed class TelegramSendResult
    {
        public bool Success { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public long? MessageId { get; private set; }

        public static TelegramSendResult Ok(long? messageId)
        {
            return new TelegramSendResult
            {
                Success = true,
                MessageId = messageId,
                Error = string.Empty
            };
        }

        public static TelegramSendResult Fail(string error)
        {
            return new TelegramSendResult
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(error) ? "Falha ao enviar mensagem." : error.Trim(),
                MessageId = null
            };
        }
    }

    private sealed class DashboardMapRow
    {
        public string BookingId { get; set; } = string.Empty;
        public string TenantId { get; set; } = "A";
        public string ServiceCategory { get; set; } = string.Empty;
        public string ServiceTitle { get; set; } = string.Empty;
        public string CustomerPhone { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public DateTime StartLocal { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string GeocodeStatus { get; set; } = string.Empty;
        public DateTimeOffset? RetryAfterUtc { get; set; }
    }

    private sealed class GeocodeResult
    {
        public bool Success { get; private set; }
        public double? Latitude { get; private set; }
        public double? Longitude { get; private set; }
        public string? ErrorMessage { get; private set; }

        public static GeocodeResult Ok(double latitude, double longitude)
        {
            return new GeocodeResult
            {
                Success = true,
                Latitude = latitude,
                Longitude = longitude
            };
        }

        public static GeocodeResult Fail(string? errorMessage)
        {
            return new GeocodeResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }
}

