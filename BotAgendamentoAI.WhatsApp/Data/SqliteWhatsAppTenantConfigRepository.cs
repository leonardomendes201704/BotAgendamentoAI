using BotAgendamentoAI.WhatsApp.Models;
using BotAgendamentoAI.WhatsApp.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BotAgendamentoAI.WhatsApp.Data;

public sealed class SqliteWhatsAppTenantConfigRepository : IWhatsAppTenantConfigRepository
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteWhatsAppTenantConfigRepository> _logger;

    public SqliteWhatsAppTenantConfigRepository(
        IOptions<WhatsAppRuntimeOptions> options,
        ILogger<SqliteWhatsAppTenantConfigRepository> logger)
    {
        _logger = logger;
        _dbPath = ResolveDatabasePath(options.Value.DatabasePath);
        _connectionString = $"Data Source={_dbPath}";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        CREATE TABLE IF NOT EXISTS tg_tenant_whatsapp_config (
          tenant_id TEXT PRIMARY KEY,
          is_active INTEGER NOT NULL DEFAULT 0,
          phone_number_id TEXT NOT NULL DEFAULT '',
          business_account_id TEXT NOT NULL DEFAULT '',
          access_token TEXT NOT NULL DEFAULT '',
          app_secret TEXT NOT NULL DEFAULT '',
          webhook_verify_token TEXT NOT NULL DEFAULT '',
          updated_at_utc TEXT NOT NULL
        );
        """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "is_active", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "phone_number_id", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "business_account_id", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "access_token", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "app_secret", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "webhook_verify_token", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        _logger.LogInformation("WhatsApp SQLite repository initialized at {Path}.", _dbPath);
    }

    public async Task<IReadOnlyList<WhatsAppTenantConfig>> GetConfigsAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var output = new List<WhatsAppTenantConfig>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        activeOnly
            ? """
        SELECT tenant_id, is_active, phone_number_id, business_account_id, access_token, app_secret, webhook_verify_token
        FROM tg_tenant_whatsapp_config
        WHERE is_active = 1
        ORDER BY tenant_id;
        """
            : """
        SELECT tenant_id, is_active, phone_number_id, business_account_id, access_token, app_secret, webhook_verify_token
        FROM tg_tenant_whatsapp_config
        ORDER BY tenant_id;
        """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new WhatsAppTenantConfig
            {
                TenantId = reader.IsDBNull(0) ? "A" : reader.GetString(0),
                IsActive = !reader.IsDBNull(1) && reader.GetInt32(1) == 1,
                PhoneNumberId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                BusinessAccountId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                AccessToken = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                AppSecret = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                WebhookVerifyToken = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
            });
        }

        return output;
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static string ResolveDatabasePath(string? rawPath)
    {
        var candidate = string.IsNullOrWhiteSpace(rawPath) ? "Data/bot.db" : rawPath.Trim();
        return Path.IsPathRooted(candidate)
            ? candidate
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, candidate));
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var exists = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
