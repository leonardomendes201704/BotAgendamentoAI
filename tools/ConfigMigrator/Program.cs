using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

const string defaultSqlitePath = "bin/Debug/net9.0/data/bot.db";

var arguments = ParseArgs(args);
var sqlServerConnectionString = BuildSqlServerConnectionString(arguments);
if (string.IsNullOrWhiteSpace(sqlServerConnectionString))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project tools/ConfigMigrator/ConfigMigrator.csproj -- --sqlserver \"<connection-string>\" [--sqlite \"<sqlite-path>\"]");
    Console.Error.WriteLine("  OR");
    Console.Error.WriteLine("  dotnet run --project tools/ConfigMigrator/ConfigMigrator.csproj -- --server \"<host,port>\" --database \"<db>\" --user \"<user>\" --password \"<password>\" [--sqlite \"<sqlite-path>\"]");
    return 1;
}

var sqlitePath = arguments.TryGetValue("sqlite", out var sqliteArg) && !string.IsNullOrWhiteSpace(sqliteArg)
    ? sqliteArg.Trim()
    : defaultSqlitePath;

var repoRoot = Directory.GetCurrentDirectory();
var resolvedSqlitePath = Path.IsPathRooted(sqlitePath)
    ? sqlitePath
    : Path.GetFullPath(Path.Combine(repoRoot, sqlitePath));

if (!File.Exists(resolvedSqlitePath))
{
    Console.Error.WriteLine($"SQLite file not found: {resolvedSqlitePath}");
    return 1;
}

await using var sqliteConnection = new SqliteConnection($"Data Source={resolvedSqlitePath}");
await sqliteConnection.OpenAsync();

if (arguments.ContainsKey("list-tables"))
{
    await using var listCommand = sqliteConnection.CreateCommand();
    listCommand.CommandText =
        """
        SELECT name
        FROM sqlite_master
        WHERE type = 'table'
        ORDER BY name;
        """;
    await using var reader = await listCommand.ExecuteReaderAsync();
    Console.WriteLine($"SQLite tables in: {resolvedSqlitePath}");
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"- {reader.GetString(0)}");
    }

    return 0;
}

await using var sqlServerConnection = new SqlConnection(sqlServerConnectionString);
await sqlServerConnection.OpenAsync();

var requiredDestinationTables = new[]
{
    "tg_service_categories",
    "tg_tenant_bot_config",
    "tg_tenant_telegram_config",
    "tg_tenant_google_calendar_config",
    "tg_shared_settings"
};

foreach (var table in requiredDestinationTables)
{
    if (!await SqlServerTableExistsAsync(sqlServerConnection, table))
    {
        Console.Error.WriteLine($"Destination table missing in SQL Server: dbo.{table}");
        return 1;
    }
}

var summaries = new List<MigrationSummary>
{
    await MigrateServiceCategoriesAsync(sqliteConnection, sqlServerConnection),
    await MigrateTenantBotConfigAsync(sqliteConnection, sqlServerConnection),
    await MigrateTenantTelegramConfigAsync(sqliteConnection, sqlServerConnection),
    await MigrateTenantGoogleCalendarConfigAsync(sqliteConnection, sqlServerConnection),
    await MigrateSharedSettingsAsync(sqliteConnection, sqlServerConnection)
};

Console.WriteLine();
Console.WriteLine("Migration summary (SQLite -> MSSQL):");
Console.WriteLine("table                                 source_rows   migrated_rows   note");
Console.WriteLine("--------------------------------------------------------------------------");
foreach (var summary in summaries)
{
    Console.WriteLine($"{summary.Table.PadRight(36)} {summary.SourceRows.ToString(CultureInfo.InvariantCulture).PadLeft(11)} {summary.MigratedRows.ToString(CultureInfo.InvariantCulture).PadLeft(15)}   {summary.Note}");
}

Console.WriteLine();
Console.WriteLine($"SQLite source: {resolvedSqlitePath}");
return 0;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var current = args[i];
        if (!current.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = current[2..];
        var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
        if (!value.StartsWith("--", StringComparison.Ordinal))
        {
            result[key] = value;
            i++;
        }
        else
        {
            result[key] = "true";
        }
    }

    return result;
}

static string BuildSqlServerConnectionString(Dictionary<string, string> arguments)
{
    if (arguments.TryGetValue("sqlserver", out var rawConnectionString) &&
        !string.IsNullOrWhiteSpace(rawConnectionString))
    {
        return rawConnectionString.Trim();
    }

    if (!arguments.TryGetValue("server", out var server) ||
        !arguments.TryGetValue("database", out var database) ||
        !arguments.TryGetValue("user", out var user) ||
        !arguments.TryGetValue("password", out var password))
    {
        return string.Empty;
    }

    var builder = new SqlConnectionStringBuilder
    {
        DataSource = server.Trim(),
        InitialCatalog = database.Trim(),
        UserID = user.Trim(),
        Password = password,
        TrustServerCertificate = GetOptionalBool(arguments, "trustServerCertificate", true),
        Encrypt = GetOptionalBool(arguments, "encrypt", true),
        MultipleActiveResultSets = GetOptionalBool(arguments, "mars", true),
        ConnectTimeout = GetOptionalInt(arguments, "timeoutSeconds", 30)
    };

    return builder.ConnectionString;
}

static bool GetOptionalBool(Dictionary<string, string> arguments, string key, bool defaultValue)
{
    return arguments.TryGetValue(key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue)
        ? bool.TryParse(rawValue, out var parsed) ? parsed : defaultValue
        : defaultValue;
}

static int GetOptionalInt(Dictionary<string, string> arguments, string key, int defaultValue)
{
    return arguments.TryGetValue(key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue)
        ? int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue
        : defaultValue;
}

static async Task<bool> SqliteTableExistsAsync(SqliteConnection connection, string tableName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@table_name LIMIT 1;";
    command.Parameters.AddWithValue("@table_name", tableName);
    var scalar = await command.ExecuteScalarAsync();
    return scalar is not null;
}

static async Task<string?> ResolveSqliteSourceTableAsync(SqliteConnection connection, params string[] candidates)
{
    foreach (var candidate in candidates)
    {
        if (await SqliteTableExistsAsync(connection, candidate))
        {
            return candidate;
        }
    }

    return null;
}

static async Task<bool> SqlServerTableExistsAsync(SqlConnection connection, string tableName)
{
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT 1
        FROM INFORMATION_SCHEMA.TABLES
        WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @table_name;
        """;
    command.Parameters.AddWithValue("@table_name", tableName);
    var scalar = await command.ExecuteScalarAsync();
    return scalar is not null;
}

static async Task<List<Dictionary<string, object?>>> ReadSqliteRowsAsync(SqliteConnection connection, string tableName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT * FROM {tableName};";
    await using var reader = await command.ExecuteReaderAsync();

    var rows = new List<Dictionary<string, object?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = await reader.IsDBNullAsync(i)
                ? null
                : reader.GetValue(i);
        }

        rows.Add(row);
    }

    return rows;
}

static string GetString(Dictionary<string, object?> row, string key, string defaultValue)
{
    return row.TryGetValue(key, out var value) && value is not null
        ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? defaultValue
        : defaultValue;
}

static int GetInt(Dictionary<string, object?> row, string key, int defaultValue)
{
    if (!row.TryGetValue(key, out var value) || value is null)
    {
        return defaultValue;
    }

    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
}

static long GetLong(Dictionary<string, object?> row, string key, long defaultValue)
{
    if (!row.TryGetValue(key, out var value) || value is null)
    {
        return defaultValue;
    }

    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
}

static int GetBit(Dictionary<string, object?> row, string key, int defaultValue)
{
    return GetInt(row, key, defaultValue) != 0 ? 1 : 0;
}

static async Task<MigrationSummary> MigrateServiceCategoriesAsync(SqliteConnection sqlite, SqlConnection sql)
{
    const string destinationTable = "tg_service_categories";
    var sourceTable = await ResolveSqliteSourceTableAsync(sqlite, "tg_service_categories", "service_categories");
    if (sourceTable is null)
    {
        return new MigrationSummary(destinationTable, 0, 0, "source_table_missing");
    }

    var rows = await ReadSqliteRowsAsync(sqlite, sourceTable);
    await using var tx = await sql.BeginTransactionAsync();

    var migrated = 0;
    try
    {
        foreach (var row in rows)
        {
            await using var command = sql.CreateCommand();
            command.Transaction = (SqlTransaction)tx;
            command.CommandText =
                """
                IF EXISTS (
                    SELECT 1
                    FROM dbo.tg_service_categories
                    WHERE tenant_id = @tenant_id
                      AND normalized_name = @normalized_name
                )
                BEGIN
                    UPDATE dbo.tg_service_categories
                    SET name = @name,
                        created_at_utc = @created_at_utc
                    WHERE tenant_id = @tenant_id
                      AND normalized_name = @normalized_name;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.tg_service_categories (tenant_id, name, normalized_name, created_at_utc)
                    VALUES (@tenant_id, @name, @normalized_name, @created_at_utc);
                END;
                """;

            command.Parameters.AddWithValue("@tenant_id", GetString(row, "tenant_id", "A"));
            command.Parameters.AddWithValue("@name", GetString(row, "name", string.Empty));
            command.Parameters.AddWithValue("@normalized_name", GetString(row, "normalized_name", string.Empty));
            command.Parameters.AddWithValue("@created_at_utc", GetString(row, "created_at_utc", DateTimeOffset.UtcNow.ToString("O")));
            await command.ExecuteNonQueryAsync();
            migrated++;
        }

        await tx.CommitAsync();
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }

    var note = sourceTable.Equals(destinationTable, StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : $"source:{sourceTable}";
    return new MigrationSummary(destinationTable, rows.Count, migrated, note);
}

static async Task<MigrationSummary> MigrateTenantBotConfigAsync(SqliteConnection sqlite, SqlConnection sql)
{
    const string destinationTable = "tg_tenant_bot_config";
    var sourceTable = await ResolveSqliteSourceTableAsync(sqlite, "tg_tenant_bot_config", "tenant_bot_config");
    if (sourceTable is null)
    {
        return new MigrationSummary(destinationTable, 0, 0, "source_table_missing");
    }

    var rows = await ReadSqliteRowsAsync(sqlite, sourceTable);
    await using var tx = await sql.BeginTransactionAsync();

    var migrated = 0;
    try
    {
        foreach (var row in rows)
        {
            await using var command = sql.CreateCommand();
            command.Transaction = (SqlTransaction)tx;
            command.CommandText =
                """
                IF EXISTS (SELECT 1 FROM dbo.tg_tenant_bot_config WHERE tenant_id = @tenant_id)
                BEGIN
                    UPDATE dbo.tg_tenant_bot_config
                    SET menu_json = @menu_json,
                        messages_json = @messages_json,
                        updated_at_utc = @updated_at_utc
                    WHERE tenant_id = @tenant_id;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.tg_tenant_bot_config (tenant_id, menu_json, messages_json, updated_at_utc)
                    VALUES (@tenant_id, @menu_json, @messages_json, @updated_at_utc);
                END;
                """;

            command.Parameters.AddWithValue("@tenant_id", GetString(row, "tenant_id", "A"));
            command.Parameters.AddWithValue("@menu_json", GetString(row, "menu_json", "{}"));
            command.Parameters.AddWithValue("@messages_json", GetString(row, "messages_json", "{}"));
            command.Parameters.AddWithValue("@updated_at_utc", GetString(row, "updated_at_utc", DateTimeOffset.UtcNow.ToString("O")));
            await command.ExecuteNonQueryAsync();
            migrated++;
        }

        await tx.CommitAsync();
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }

    var note = sourceTable.Equals(destinationTable, StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : $"source:{sourceTable}";
    return new MigrationSummary(destinationTable, rows.Count, migrated, note);
}

static async Task<MigrationSummary> MigrateTenantTelegramConfigAsync(SqliteConnection sqlite, SqlConnection sql)
{
    const string destinationTable = "tg_tenant_telegram_config";
    var sourceTable = await ResolveSqliteSourceTableAsync(sqlite, "tg_tenant_telegram_config", "tenant_telegram_config");
    if (sourceTable is null)
    {
        return new MigrationSummary(destinationTable, 0, 0, "source_table_missing");
    }

    var rows = await ReadSqliteRowsAsync(sqlite, sourceTable);
    await using var tx = await sql.BeginTransactionAsync();

    var migrated = 0;
    try
    {
        foreach (var row in rows)
        {
            await using var command = sql.CreateCommand();
            command.Transaction = (SqlTransaction)tx;
            command.CommandText =
                """
                IF EXISTS (SELECT 1 FROM dbo.tg_tenant_telegram_config WHERE tenant_id = @tenant_id)
                BEGIN
                    UPDATE dbo.tg_tenant_telegram_config
                    SET bot_id = @bot_id,
                        bot_username = @bot_username,
                        bot_token = @bot_token,
                        is_active = @is_active,
                        polling_timeout_seconds = @polling_timeout_seconds,
                        last_update_id = @last_update_id,
                        updated_at_utc = @updated_at_utc
                    WHERE tenant_id = @tenant_id;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.tg_tenant_telegram_config
                    (
                        tenant_id, bot_id, bot_username, bot_token, is_active,
                        polling_timeout_seconds, last_update_id, updated_at_utc
                    )
                    VALUES
                    (
                        @tenant_id, @bot_id, @bot_username, @bot_token, @is_active,
                        @polling_timeout_seconds, @last_update_id, @updated_at_utc
                    );
                END;
                """;

            command.Parameters.AddWithValue("@tenant_id", GetString(row, "tenant_id", "A"));
            command.Parameters.AddWithValue("@bot_id", GetString(row, "bot_id", string.Empty));
            command.Parameters.AddWithValue("@bot_username", GetString(row, "bot_username", string.Empty));
            command.Parameters.AddWithValue("@bot_token", GetString(row, "bot_token", string.Empty));
            command.Parameters.AddWithValue("@is_active", GetBit(row, "is_active", 0));
            command.Parameters.AddWithValue("@polling_timeout_seconds", GetInt(row, "polling_timeout_seconds", 30));
            command.Parameters.AddWithValue("@last_update_id", GetLong(row, "last_update_id", 0));
            command.Parameters.AddWithValue("@updated_at_utc", GetString(row, "updated_at_utc", DateTimeOffset.UtcNow.ToString("O")));
            await command.ExecuteNonQueryAsync();
            migrated++;
        }

        await tx.CommitAsync();
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }

    var note = sourceTable.Equals(destinationTable, StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : $"source:{sourceTable}";
    return new MigrationSummary(destinationTable, rows.Count, migrated, note);
}

static async Task<MigrationSummary> MigrateTenantGoogleCalendarConfigAsync(SqliteConnection sqlite, SqlConnection sql)
{
    const string destinationTable = "tg_tenant_google_calendar_config";
    var sourceTable = await ResolveSqliteSourceTableAsync(sqlite, "tg_tenant_google_calendar_config", "tenant_google_calendar_config");
    if (sourceTable is null)
    {
        return new MigrationSummary(destinationTable, 0, 0, "source_table_missing");
    }

    var rows = await ReadSqliteRowsAsync(sqlite, sourceTable);
    await using var tx = await sql.BeginTransactionAsync();

    var migrated = 0;
    try
    {
        foreach (var row in rows)
        {
            await using var command = sql.CreateCommand();
            command.Transaction = (SqlTransaction)tx;
            command.CommandText =
                """
                IF EXISTS (SELECT 1 FROM dbo.tg_tenant_google_calendar_config WHERE tenant_id = @tenant_id)
                BEGIN
                    UPDATE dbo.tg_tenant_google_calendar_config
                    SET is_enabled = @is_enabled,
                        calendar_id = @calendar_id,
                        service_account_json = @service_account_json,
                        time_zone_id = @time_zone_id,
                        default_duration_minutes = @default_duration_minutes,
                        availability_window_days = @availability_window_days,
                        availability_slot_interval_minutes = @availability_slot_interval_minutes,
                        availability_workday_start_hour = @availability_workday_start_hour,
                        availability_workday_end_hour = @availability_workday_end_hour,
                        availability_today_lead_minutes = @availability_today_lead_minutes,
                        max_attempts = @max_attempts,
                        retry_base_seconds = @retry_base_seconds,
                        retry_max_seconds = @retry_max_seconds,
                        event_title_template = @event_title_template,
                        event_description_template = @event_description_template,
                        updated_at_utc = @updated_at_utc
                    WHERE tenant_id = @tenant_id;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.tg_tenant_google_calendar_config
                    (
                        tenant_id, is_enabled, calendar_id, service_account_json, time_zone_id,
                        default_duration_minutes, availability_window_days, availability_slot_interval_minutes,
                        availability_workday_start_hour, availability_workday_end_hour, availability_today_lead_minutes,
                        max_attempts, retry_base_seconds, retry_max_seconds, event_title_template,
                        event_description_template, updated_at_utc
                    )
                    VALUES
                    (
                        @tenant_id, @is_enabled, @calendar_id, @service_account_json, @time_zone_id,
                        @default_duration_minutes, @availability_window_days, @availability_slot_interval_minutes,
                        @availability_workday_start_hour, @availability_workday_end_hour, @availability_today_lead_minutes,
                        @max_attempts, @retry_base_seconds, @retry_max_seconds, @event_title_template,
                        @event_description_template, @updated_at_utc
                    );
                END;
                """;

            command.Parameters.AddWithValue("@tenant_id", GetString(row, "tenant_id", "A"));
            command.Parameters.AddWithValue("@is_enabled", GetBit(row, "is_enabled", 0));
            command.Parameters.AddWithValue("@calendar_id", GetString(row, "calendar_id", string.Empty));
            command.Parameters.AddWithValue("@service_account_json", GetString(row, "service_account_json", string.Empty));
            command.Parameters.AddWithValue("@time_zone_id", GetString(row, "time_zone_id", "America/Sao_Paulo"));
            command.Parameters.AddWithValue("@default_duration_minutes", GetInt(row, "default_duration_minutes", 60));
            command.Parameters.AddWithValue("@availability_window_days", GetInt(row, "availability_window_days", 7));
            command.Parameters.AddWithValue("@availability_slot_interval_minutes", GetInt(row, "availability_slot_interval_minutes", 60));
            command.Parameters.AddWithValue("@availability_workday_start_hour", GetInt(row, "availability_workday_start_hour", 8));
            command.Parameters.AddWithValue("@availability_workday_end_hour", GetInt(row, "availability_workday_end_hour", 20));
            command.Parameters.AddWithValue("@availability_today_lead_minutes", GetInt(row, "availability_today_lead_minutes", 30));
            command.Parameters.AddWithValue("@max_attempts", GetInt(row, "max_attempts", 8));
            command.Parameters.AddWithValue("@retry_base_seconds", GetInt(row, "retry_base_seconds", 10));
            command.Parameters.AddWithValue("@retry_max_seconds", GetInt(row, "retry_max_seconds", 600));
            command.Parameters.AddWithValue("@event_title_template", GetString(row, "event_title_template", string.Empty));
            command.Parameters.AddWithValue("@event_description_template", GetString(row, "event_description_template", string.Empty));
            command.Parameters.AddWithValue("@updated_at_utc", GetString(row, "updated_at_utc", DateTimeOffset.UtcNow.ToString("O")));
            await command.ExecuteNonQueryAsync();
            migrated++;
        }

        await tx.CommitAsync();
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }

    var note = sourceTable.Equals(destinationTable, StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : $"source:{sourceTable}";
    return new MigrationSummary(destinationTable, rows.Count, migrated, note);
}

static async Task<MigrationSummary> MigrateSharedSettingsAsync(SqliteConnection sqlite, SqlConnection sql)
{
    const string destinationTable = "tg_shared_settings";
    var sourceTable = await ResolveSqliteSourceTableAsync(sqlite, "tg_shared_settings", "shared_settings");
    if (sourceTable is null)
    {
        return new MigrationSummary(destinationTable, 0, 0, "source_table_missing");
    }

    var rows = await ReadSqliteRowsAsync(sqlite, sourceTable);
    await using var tx = await sql.BeginTransactionAsync();

    var migrated = 0;
    try
    {
        foreach (var row in rows)
        {
            await using var command = sql.CreateCommand();
            command.Transaction = (SqlTransaction)tx;
            command.CommandText =
                """
                IF EXISTS (SELECT 1 FROM dbo.tg_shared_settings WHERE setting_key = @setting_key)
                BEGIN
                    UPDATE dbo.tg_shared_settings
                    SET setting_value = @setting_value,
                        updated_at_utc = @updated_at_utc
                    WHERE setting_key = @setting_key;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.tg_shared_settings (setting_key, setting_value, updated_at_utc)
                    VALUES (@setting_key, @setting_value, @updated_at_utc);
                END;
                """;

            command.Parameters.AddWithValue("@setting_key", GetString(row, "setting_key", string.Empty));
            command.Parameters.AddWithValue("@setting_value", GetString(row, "setting_value", string.Empty));
            command.Parameters.AddWithValue("@updated_at_utc", GetString(row, "updated_at_utc", DateTimeOffset.UtcNow.ToString("O")));
            await command.ExecuteNonQueryAsync();
            migrated++;
        }

        await tx.CommitAsync();
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }

    var note = sourceTable.Equals(destinationTable, StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : $"source:{sourceTable}";
    return new MigrationSummary(destinationTable, rows.Count, migrated, note);
}

public sealed record MigrationSummary(
    string Table,
    int SourceRows,
    int MigratedRows,
    string Note);
