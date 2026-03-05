using System.Globalization;
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

    private const string AdminSchemaSql = """
CREATE TABLE IF NOT EXISTS conversation_messages (
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
ON conversation_messages(tenant_id, phone, created_at_utc);

CREATE TABLE IF NOT EXISTS conversation_state (
  tenant_id TEXT NOT NULL,
  phone TEXT NOT NULL,
  summary TEXT NOT NULL,
  slots_json TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY (tenant_id, phone)
);

CREATE TABLE IF NOT EXISTS service_categories (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  tenant_id TEXT NOT NULL,
  name TEXT NOT NULL,
  normalized_name TEXT NOT NULL,
  created_at_utc TEXT NOT NULL,
  UNIQUE(tenant_id, normalized_name)
);

CREATE INDEX IF NOT EXISTS idx_service_categories_tenant_name
ON service_categories(tenant_id, name);

CREATE TABLE IF NOT EXISTS bookings (
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
ON bookings(tenant_id, customer_phone, start_local);

CREATE INDEX IF NOT EXISTS idx_bookings_tenant_start
ON bookings(tenant_id, start_local);

CREATE TABLE IF NOT EXISTS service_catalog (
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
ON service_catalog(tenant_id, is_active, title);

CREATE TABLE IF NOT EXISTS tenant_bot_config (
  tenant_id TEXT PRIMARY KEY,
  menu_json TEXT NOT NULL,
  messages_json TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tenant_telegram_config (
  tenant_id TEXT PRIMARY KEY,
  bot_id TEXT NOT NULL,
  bot_username TEXT NOT NULL,
  bot_token TEXT NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 0,
  polling_timeout_seconds INTEGER NOT NULL DEFAULT 30,
  last_update_id INTEGER NOT NULL DEFAULT 0,
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

        _logger.LogInformation("Admin SQLite path: {Path}", _dbPath);
    }

    public async Task<IReadOnlyList<string>> GetTenantIdsAsync()
    {
        var tenants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        async Task CollectAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
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

        await CollectAsync("SELECT DISTINCT tenant_id FROM conversation_messages;");
        await CollectAsync("SELECT DISTINCT tenant_id FROM bookings;");
        await CollectAsync("SELECT DISTINCT tenant_id FROM conversation_state;");
        await CollectAsync("SELECT DISTINCT tenant_id FROM tenant_bot_config;");
        await CollectAsync("SELECT DISTINCT tenant_id FROM tenant_telegram_config;");

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

        var totalIncomingConversations = await QueryIntAsync(connection,
            """
            SELECT COUNT(DISTINCT phone)
            FROM conversation_messages
            WHERE tenant_id = @tenant_id
              AND role = 'user'
              AND direction = 'in'
              AND created_at_utc >= @from_utc
              AND created_at_utc <= @to_utc;
            """,
            ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

        var totalMessages = await QueryIntAsync(connection,
            """
            SELECT COUNT(*)
            FROM conversation_messages
            WHERE tenant_id = @tenant_id
              AND created_at_utc >= @from_utc
              AND created_at_utc <= @to_utc;
            """,
            ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

        var createdBookings = await QueryIntAsync(connection,
            """
            SELECT COUNT(*)
            FROM bookings
            WHERE tenant_id = @tenant_id
              AND created_at_utc >= @from_utc
              AND created_at_utc <= @to_utc;
            """,
            ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

        var convertedPhones = await QueryIntAsync(connection,
            """
            SELECT COUNT(DISTINCT customer_phone)
            FROM bookings
            WHERE tenant_id = @tenant_id
              AND created_at_utc >= @from_utc
              AND created_at_utc <= @to_utc;
            """,
            ("@tenant_id", tenant), ("@from_utc", fromText), ("@to_utc", nowText));

        var humanHandoffOpen = await QueryIntAsync(connection,
            """
            SELECT COUNT(*)
            FROM conversation_state
            WHERE tenant_id = @tenant_id
              AND (
                json_extract(slots_json, '$.menuContext') = 'human_handoff'
                OR json_extract(slots_json, '$.pending') = 'human_handoff'
              );
            """,
            ("@tenant_id", tenant));

        var recentConversations = await GetConversationThreadsAsync(tenant, 20);
        var recentBookings = await GetBookingsAsync(tenant, 20);

        var conversionRate = totalIncomingConversations == 0
            ? 0m
            : Math.Round(convertedPhones * 100m / totalIncomingConversations, 2);

        return new DashboardViewModel
        {
            TenantId = tenant,
            Days = safeDays,
            FromUtc = fromUtc,
            ToUtc = nowUtc,
            TotalIncomingConversations = totalIncomingConversations,
            TotalMessages = totalMessages,
            CreatedBookings = createdBookings,
            HumanHandoffOpen = humanHandoffOpen,
            ConvertedPhones = convertedPhones,
            ConversionRatePercent = conversionRate,
            RecentConversations = recentConversations,
            RecentBookings = recentBookings
        };
    }

    public async Task<IReadOnlyList<ConversationThreadSummary>> GetConversationThreadsAsync(string tenantId, int limit)
    {
        var output = new List<ConversationThreadSummary>();
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 500);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                t.phone,
                t.last_message_at_utc,
                COALESCE(last_msg.content, '') AS last_content,
                COALESCE(json_extract(cs.slots_json, '$.menuContext'), '') AS menu_context
            FROM (
                SELECT phone, MAX(created_at_utc) AS last_message_at_utc
                FROM conversation_messages
                WHERE tenant_id = @tenant_id
                GROUP BY phone
                ORDER BY last_message_at_utc DESC
                LIMIT @limit
            ) t
            LEFT JOIN conversation_messages last_msg
                ON last_msg.tenant_id = @tenant_id
               AND last_msg.phone = t.phone
               AND last_msg.created_at_utc = t.last_message_at_utc
            LEFT JOIN conversation_state cs
                ON cs.tenant_id = @tenant_id
               AND cs.phone = t.phone
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
                IsInHumanHandoff = string.Equals(menuContext, "human_handoff", StringComparison.OrdinalIgnoreCase)
            });
        }

        return output;
    }

    public async Task<IReadOnlyList<ConversationMessageItem>> GetConversationMessagesAsync(string tenantId, string phone, int limit)
    {
        var output = new List<ConversationMessageItem>();
        var tenant = NormalizeTenant(tenantId);
        var normalizedPhone = phone.Trim();
        var safeLimit = Math.Clamp(limit, 1, 1000);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, direction, role, content, tool_name, tool_call_id, created_at_utc
            FROM conversation_messages
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

        output.Reverse();
        return output;
    }

    public async Task<IReadOnlyList<BookingListItem>> GetBookingsAsync(string tenantId, int limit)
    {
        var output = new List<BookingListItem>();
        var tenant = NormalizeTenant(tenantId);
        var safeLimit = Math.Clamp(limit, 1, 1000);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, customer_phone, customer_name, service_category, service_title, start_local, duration_minutes, address, technician_name, created_at_utc
            FROM bookings
            WHERE tenant_id = @tenant_id
            ORDER BY created_at_utc DESC
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
                CustomerPhone = reader.GetString(1),
                CustomerName = reader.GetString(2),
                ServiceCategory = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                ServiceTitle = reader.GetString(4),
                StartLocal = ParseLocalDateTime(reader.GetString(5)),
                DurationMinutes = reader.GetInt32(6),
                Address = reader.GetString(7),
                TechnicianName = reader.GetString(8),
                CreatedAtUtc = ParseUtc(reader.GetString(9))
            });
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
            FROM service_categories
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
            FROM service_categories
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
                INSERT INTO service_categories (tenant_id, name, normalized_name, created_at_utc)
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
                UPDATE service_categories
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
            FROM service_categories
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
                UPDATE service_catalog
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
                DELETE FROM service_categories
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
            FROM service_catalog
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
            FROM service_catalog
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
                INSERT INTO service_catalog
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
                UPDATE service_catalog
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
            DELETE FROM service_catalog
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
            FROM tenant_bot_config
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
                model.ClosingText = messages.ClosingText;
                model.FallbackText = messages.FallbackText;
                model.MessagePoolingSeconds = ClampPoolingSeconds(messages.MessagePoolingSeconds);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT bot_id, bot_username, bot_token, is_active, polling_timeout_seconds, last_update_id
            FROM tenant_telegram_config
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

        return model;
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
            ClosingText = input.ClosingText?.Trim() ?? string.Empty,
            FallbackText = input.FallbackText?.Trim() ?? string.Empty,
            MessagePoolingSeconds = ClampPoolingSeconds(input.MessagePoolingSeconds)
        };
        var telegram = new TelegramConfigStorage
        {
            BotId = input.TelegramBotId?.Trim() ?? string.Empty,
            BotUsername = input.TelegramBotUsername?.Trim() ?? string.Empty,
            BotToken = input.TelegramBotToken?.Trim() ?? string.Empty,
            IsActive = input.TelegramIsActive,
            PollingTimeoutSeconds = ClampTelegramPollingSeconds(input.TelegramPollingTimeoutSeconds),
            LastUpdateId = Math.Max(0L, input.TelegramLastUpdateId)
        };
        var nowUtc = ToUtcText(DateTimeOffset.UtcNow);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            INSERT INTO tenant_bot_config (tenant_id, menu_json, messages_json, updated_at_utc)
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
            INSERT INTO tenant_telegram_config
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
            MainMenuText = "1 - Agendar Servico\n2 - Consultar Agendamentos\n3 - Cancelar Agendamento\n4 - Alterar Agendamento\n5 - Falar com atendente\n6 - Encerrar atendimento",
            GreetingText = "Como posso ajudar voce hoje?",
            HumanHandoffText = "Vou te direcionar para um atendente humano.",
            ClosingText = "Atendimento encerrado. Envie MENU para iniciar novamente.",
            FallbackText = "Nao entendi. Escolha uma opcao do menu.",
            MessagePoolingSeconds = 15,
            TelegramPollingTimeoutSeconds = 30
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

    private static int ClampTelegramPollingSeconds(int value)
        => Math.Clamp(value, 5, 50);

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
            FROM service_categories
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

    private SqliteConnection CreateConnection() => new(_connectionString);

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

    private sealed class MenuConfigStorage
    {
        public string MainMenuText { get; set; } = string.Empty;
    }

    private sealed class MessagesConfigStorage
    {
        public string GreetingText { get; set; } = string.Empty;
        public string HumanHandoffText { get; set; } = string.Empty;
        public string ClosingText { get; set; } = string.Empty;
        public string FallbackText { get; set; } = string.Empty;
        public int? MessagePoolingSeconds { get; set; }
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
}
