using System.Globalization;
using BotAgendamentoAI.Domain;
using Microsoft.Data.Sqlite;

namespace BotAgendamentoAI.Data;

public sealed class ConversationRepository
{
    private readonly string _connectionString;

    private const string SchemaSql = """
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
            INSERT INTO conversation_messages
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
            FROM conversation_messages
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
            FROM conversation_messages
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
            FROM conversation_state
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
            INSERT INTO conversation_state
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
}
