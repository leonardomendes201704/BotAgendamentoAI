using BotAgendamentoAI.WhatsApp.Models;
using Microsoft.Data.SqlClient;

namespace BotAgendamentoAI.WhatsApp.Data;

public sealed class SqlServerWhatsAppTenantConfigRepository : IWhatsAppTenantConfigRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerWhatsAppTenantConfigRepository> _logger;

    public SqlServerWhatsAppTenantConfigRepository(
        IConfiguration configuration,
        ILogger<SqlServerWhatsAppTenantConfigRepository> logger)
    {
        _logger = logger;
        _connectionString = ResolveConnectionString(configuration.GetConnectionString("DefaultConnection"));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        IF OBJECT_ID(N'dbo.tg_tenant_whatsapp_config', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.tg_tenant_whatsapp_config
            (
                tenant_id NVARCHAR(32) PRIMARY KEY,
                is_active BIT NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_is_active_runtime DEFAULT(0),
                phone_number_id NVARCHAR(128) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_phone_number_id_runtime DEFAULT(N''),
                business_account_id NVARCHAR(128) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_business_account_id_runtime DEFAULT(N''),
                access_token NVARCHAR(MAX) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_access_token_runtime DEFAULT(N''),
                app_secret NVARCHAR(256) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_app_secret_runtime DEFAULT(N''),
                webhook_verify_token NVARCHAR(256) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_webhook_verify_token_runtime DEFAULT(N''),
                updated_at_utc NVARCHAR(64) NOT NULL
            );
        END;
        """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "is_active", "BIT NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_is_active_runtime2 DEFAULT(0)", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "phone_number_id", "NVARCHAR(128) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_phone_number_id_runtime2 DEFAULT(N'')", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "business_account_id", "NVARCHAR(128) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_business_account_id_runtime2 DEFAULT(N'')", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "access_token", "NVARCHAR(MAX) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_access_token_runtime2 DEFAULT(N'')", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "app_secret", "NVARCHAR(256) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_app_secret_runtime2 DEFAULT(N'')", cancellationToken);
        await EnsureColumnAsync(connection, "tg_tenant_whatsapp_config", "webhook_verify_token", "NVARCHAR(256) NOT NULL CONSTRAINT DF_tg_tenant_whatsapp_config_webhook_verify_token_runtime2 DEFAULT(N'')", cancellationToken);

        _logger.LogInformation("WhatsApp SQL Server repository initialized.");
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
                IsActive = !reader.IsDBNull(1) && ReadBool(reader.GetValue(1)),
                PhoneNumberId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                BusinessAccountId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                AccessToken = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                AppSecret = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                WebhookVerifyToken = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
            });
        }

        return output;
    }

    private SqlConnection CreateConnection() => new(_connectionString);

    private static string ResolveConnectionString(string? defaultConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(defaultConnectionString))
        {
            return defaultConnectionString.Trim();
        }

        throw new InvalidOperationException("Connection string not configured for WhatsApp SQL Server repository.");
    }

    private static bool ReadBool(object value)
    {
        return value switch
        {
            bool boolValue => boolValue,
            byte byteValue => byteValue != 0,
            short shortValue => shortValue != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            string text when bool.TryParse(text, out var parsedBool) => parsedBool,
            string text when int.TryParse(text, out var parsedInt) => parsedInt != 0,
            IConvertible convertible => convertible.ToInt32(null) != 0,
            _ => false
        };
    }

    private static async Task EnsureColumnAsync(
        SqlConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        $"""
        IF COL_LENGTH(N'dbo.{tableName}', N'{columnName}') IS NULL
        BEGIN
            ALTER TABLE dbo.{tableName}
            ADD {columnName} {columnDefinition};
        END;
        """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
