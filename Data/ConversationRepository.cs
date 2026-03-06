using System.Globalization;
using System.Text.Json;
using BotAgendamentoAI.Domain;
using Microsoft.Data.Sqlite;

namespace BotAgendamentoAI.Data;

public sealed class ConversationRepository
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string SchemaSql = """
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

CREATE TABLE IF NOT EXISTS tg_shared_settings (
  setting_key TEXT PRIMARY KEY,
  setting_value TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL
);
""";

    public ConversationRepository(string sqlitePath)
    {
        var fullPath = Path.GetFullPath(sqlitePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = $"Data Source={fullPath}";
    }

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task AddMessage(ConversationMessage message)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tg_conversation_messages
            (tenant_id, phone, direction, role, content, tool_name, tool_call_id, created_at_utc, metadata_json)
            VALUES
            (@tenant_id, @phone, @direction, @role, @content, @tool_name, @tool_call_id, @created_at_utc, @metadata_json);
            """;

        command.Parameters.AddWithValue("@tenant_id", message.TenantId);
        command.Parameters.AddWithValue("@phone", message.Phone);
        command.Parameters.AddWithValue("@direction", message.Direction);
        command.Parameters.AddWithValue("@role", message.Role);
        command.Parameters.AddWithValue("@content", message.Content ?? string.Empty);
        command.Parameters.AddWithValue("@tool_name", (object?)message.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue("@tool_call_id", (object?)message.ToolCallId ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at_utc", ToUtcText(message.CreatedAtUtc));
        command.Parameters.AddWithValue("@metadata_json", (object?)message.MetadataJson ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetFullHistory(string tenantId, string phone)
    {
        var output = new List<ConversationMessage>();

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, phone, direction, role, content, tool_name, tool_call_id, created_at_utc, metadata_json
            FROM tg_conversation_messages
            WHERE tenant_id = @tenant_id AND phone = @phone
            ORDER BY created_at_utc ASC, id ASC;
            """;

        command.Parameters.AddWithValue("@tenant_id", tenantId);
        command.Parameters.AddWithValue("@phone", phone);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.Add(ReadMessage(reader));
        }

        return output;
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetLast24h(
        string tenantId,
        string phone,
        int limit,
        DateTimeOffset nowUtc)
    {
        var output = new List<ConversationMessage>();
        var start = nowUtc.AddHours(-24);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, phone, direction, role, content, tool_name, tool_call_id, created_at_utc, metadata_json
            FROM tg_conversation_messages
            WHERE tenant_id = @tenant_id
              AND phone = @phone
              AND created_at_utc >= @start_utc
            ORDER BY created_at_utc DESC, id DESC
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@tenant_id", tenantId);
        command.Parameters.AddWithValue("@phone", phone);
        command.Parameters.AddWithValue("@start_utc", ToUtcText(start));
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.Add(ReadMessage(reader));
        }

        output.Reverse();
        return output;
    }

    public async Task<ConversationState?> GetState(string tenantId, string phone)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT tenant_id, phone, summary, slots_json, updated_at_utc
            FROM tg_conversation_state
            WHERE tenant_id = @tenant_id AND phone = @phone
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("@tenant_id", tenantId);
        command.Parameters.AddWithValue("@phone", phone);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ConversationState
        {
            TenantId = reader.GetString(0),
            Phone = reader.GetString(1),
            Summary = reader.GetString(2),
            SlotsJson = reader.GetString(3),
            UpdatedAtUtc = ParseUtc(reader.GetString(4))
        };
    }

    public async Task UpsertState(ConversationState state)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tg_conversation_state
            (tenant_id, phone, summary, slots_json, updated_at_utc)
            VALUES
            (@tenant_id, @phone, @summary, @slots_json, @updated_at_utc)
            ON CONFLICT(tenant_id, phone)
            DO UPDATE SET
                summary = excluded.summary,
                slots_json = excluded.slots_json,
                updated_at_utc = excluded.updated_at_utc;
            """;

        command.Parameters.AddWithValue("@tenant_id", state.TenantId);
        command.Parameters.AddWithValue("@phone", state.Phone);
        command.Parameters.AddWithValue("@summary", state.Summary ?? string.Empty);
        command.Parameters.AddWithValue("@slots_json", state.SlotsJson ?? "{}");
        command.Parameters.AddWithValue("@updated_at_utc", ToUtcText(state.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<BotTextConfig> GetBotTextConfig(string tenantId)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT menu_json, messages_json
            FROM tg_tenant_bot_config
            WHERE tenant_id = @tenant_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return BotTextConfig.CreateDefault(tenant);
        }

        var menuJson = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var messagesJson = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

        var config = BotTextConfig.CreateDefault(tenant);

        try
        {
            var menu = JsonSerializer.Deserialize<MenuConfigStorage>(menuJson, ConfigJsonOptions);
            if (!string.IsNullOrWhiteSpace(menu?.MainMenuText))
            {
                config.MainMenuText = menu.MainMenuText.Trim();
            }
        }
        catch
        {
            // Keep default menu text.
        }

        try
        {
            var messages = JsonSerializer.Deserialize<MessagesConfigStorage>(messagesJson, ConfigJsonOptions);
            if (!string.IsNullOrWhiteSpace(messages?.GreetingText))
            {
                config.GreetingText = messages.GreetingText.Trim();
            }

            if (!string.IsNullOrWhiteSpace(messages?.HumanHandoffText))
            {
                config.HumanHandoffText = messages.HumanHandoffText.Trim();
            }

            if (!string.IsNullOrWhiteSpace(messages?.ClosingText))
            {
                config.ClosingText = messages.ClosingText.Trim();
            }

            if (!string.IsNullOrWhiteSpace(messages?.FallbackText))
            {
                config.FallbackText = messages.FallbackText.Trim();
            }

            config.MessagePoolingSeconds = ClampPoolingSeconds(messages?.MessagePoolingSeconds);
        }
        catch
        {
            // Keep default message texts.
        }

        return config;
    }

    public async Task<TelegramBotConfig> GetTelegramConfig(string tenantId)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT tenant_id, bot_id, bot_username, bot_token, is_active, polling_timeout_seconds, last_update_id, updated_at_utc
            FROM tg_tenant_telegram_config
            WHERE tenant_id = @tenant_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return TelegramBotConfig.CreateDefault(tenant);
        }

        return new TelegramBotConfig
        {
            TenantId = reader.GetString(0),
            BotId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            BotUsername = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            BotToken = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            IsActive = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
            PollingTimeoutSeconds = ClampTelegramPollingSeconds(reader.IsDBNull(5) ? 30 : reader.GetInt32(5)),
            LastUpdateId = reader.IsDBNull(6) ? 0L : reader.GetInt64(6),
            UpdatedAtUtc = ParseUtc(reader.IsDBNull(7) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(7))
        };
    }

    public async Task<IReadOnlyList<TelegramBotConfig>> GetActiveTelegramConfigs()
    {
        var output = new List<TelegramBotConfig>();

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT tenant_id, bot_id, bot_username, bot_token, is_active, polling_timeout_seconds, last_update_id, updated_at_utc
            FROM tg_tenant_telegram_config
            WHERE is_active = 1
              AND LENGTH(TRIM(bot_token)) > 0;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.Add(new TelegramBotConfig
            {
                TenantId = reader.GetString(0),
                BotId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                BotUsername = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                BotToken = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                IsActive = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
                PollingTimeoutSeconds = ClampTelegramPollingSeconds(reader.IsDBNull(5) ? 30 : reader.GetInt32(5)),
                LastUpdateId = reader.IsDBNull(6) ? 0L : reader.GetInt64(6),
                UpdatedAtUtc = ParseUtc(reader.IsDBNull(7) ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : reader.GetString(7))
            });
        }

        return output;
    }

    public async Task UpsertTelegramConfig(TelegramBotConfig config)
    {
        var tenant = string.IsNullOrWhiteSpace(config.TenantId) ? "A" : config.TenantId.Trim();
        var updatedAtUtc = config.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : config.UpdatedAtUtc;

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tg_tenant_telegram_config
            (tenant_id, bot_id, bot_username, bot_token, is_active, polling_timeout_seconds, last_update_id, updated_at_utc)
            VALUES
            (@tenant_id, @bot_id, @bot_username, @bot_token, @is_active, @polling_timeout_seconds, @last_update_id, @updated_at_utc)
            ON CONFLICT(tenant_id)
            DO UPDATE SET
              bot_id = excluded.bot_id,
              bot_username = excluded.bot_username,
              bot_token = excluded.bot_token,
              is_active = excluded.is_active,
              polling_timeout_seconds = excluded.polling_timeout_seconds,
              last_update_id = excluded.last_update_id,
              updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@bot_id", config.BotId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@bot_username", config.BotUsername?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@bot_token", config.BotToken?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@is_active", config.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("@polling_timeout_seconds", ClampTelegramPollingSeconds(config.PollingTimeoutSeconds));
        command.Parameters.AddWithValue("@last_update_id", Math.Max(0L, config.LastUpdateId));
        command.Parameters.AddWithValue("@updated_at_utc", ToUtcText(updatedAtUtc));

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateTelegramLastUpdateId(string tenantId, long lastUpdateId)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tg_tenant_telegram_config
            SET last_update_id = @last_update_id,
                updated_at_utc = @updated_at_utc
            WHERE tenant_id = @tenant_id;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);
        command.Parameters.AddWithValue("@last_update_id", Math.Max(0L, lastUpdateId));
        command.Parameters.AddWithValue("@updated_at_utc", ToUtcText(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<string> GetOpenAiApiKey()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT setting_value
            FROM tg_shared_settings
            WHERE setting_key = @setting_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@setting_key", OpenAiApiKeySettingKey);

        var value = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return (value ?? string.Empty).Trim();
    }

    public async Task UpsertOpenAiApiKey(string apiKey)
    {
        var safeValue = (apiKey ?? string.Empty).Trim();
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tg_shared_settings (setting_key, setting_value, updated_at_utc)
            VALUES (@setting_key, @setting_value, @updated_at_utc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("@setting_key", OpenAiApiKeySettingKey);
        command.Parameters.AddWithValue("@setting_value", safeValue);
        command.Parameters.AddWithValue("@updated_at_utc", ToUtcText(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static string ToUtcText(DateTimeOffset dateTimeOffset)
        => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    private static ConversationMessage ReadMessage(SqliteDataReader reader)
    {
        return new ConversationMessage
        {
            Id = reader.GetInt64(0),
            TenantId = reader.GetString(1),
            Phone = reader.GetString(2),
            Direction = reader.GetString(3),
            Role = reader.GetString(4),
            Content = reader.GetString(5),
            ToolName = reader.IsDBNull(6) ? null : reader.GetString(6),
            ToolCallId = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAtUtc = ParseUtc(reader.GetString(8)),
            MetadataJson = reader.IsDBNull(9) ? null : reader.GetString(9)
        };
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

    private static int ClampPoolingSeconds(int? value)
        => Math.Clamp(value ?? 15, 0, 120);

    private static int ClampTelegramPollingSeconds(int value)
        => Math.Clamp(value, 5, 50);

    private const string OpenAiApiKeySettingKey = "openai_api_key";
}

