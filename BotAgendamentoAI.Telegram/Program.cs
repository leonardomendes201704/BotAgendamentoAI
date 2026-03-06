using BotAgendamentoAI.Telegram;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Features.Client;
using BotAgendamentoAI.Telegram.Features.Provider;
using BotAgendamentoAI.Telegram.Features.Shared;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using BotAgendamentoAI.Telegram.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<TelegramWorkerOptions>(builder.Configuration.GetSection("TelegramWorker"));

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Services.AddSerilog();

var rawOptions = builder.Configuration.GetSection("TelegramWorker").Get<TelegramWorkerOptions>() ?? new TelegramWorkerOptions();
var databasePath = ResolveDatabasePath(rawOptions.DatabasePath);
var timeZone = ResolveTimeZone(rawOptions.TimeZoneId);

builder.Services.AddSingleton(new TelegramRuntimeSettings
{
    DatabasePath = databasePath,
    TimeZone = timeZone,
    TenantIdleDelaySeconds = Math.Clamp(rawOptions.TenantIdleDelaySeconds, 1, 60),
    SessionExpiryMinutes = Math.Clamp(rawOptions.SessionExpiryMinutes, 5, 1440),
    HistoryLimitPerContext = Math.Clamp(rawOptions.HistoryLimitPerContext, 5, 200),
    EnablePhotoValidation = rawOptions.EnablePhotoValidation
});

builder.Services.AddDbContextFactory<BotDbContext>(options =>
{
    options.UseSqlite($"Data Source={databasePath}");
});

builder.Services.AddHttpClient<TelegramApiClient>();
builder.Services.AddSingleton<TenantConfigService>();
builder.Services.AddSingleton<UserContextService>();
builder.Services.AddSingleton<ConversationHistoryService>();
builder.Services.AddSingleton<TelegramMessageSender>();
builder.Services.AddSingleton<BotExceptionLogService>();
builder.Services.AddSingleton<AvailabilityService>();
builder.Services.AddSingleton<CalendarSyncQueueService>();
builder.Services.AddSingleton<GoogleCalendarApiService>();
builder.Services.AddSingleton<JobWorkflowService>();
builder.Services.AddSingleton<IPhotoValidator, StubPhotoValidator>();
builder.Services.AddSingleton<ChatMediatorService>();
builder.Services.AddSingleton<ClientFlowHandler>();
builder.Services.AddSingleton<ProviderFlowHandler>();
builder.Services.AddSingleton<MarketplaceBotOrchestrator>();
builder.Services.AddHostedService<TelegramPollingWorker>();
builder.Services.AddHostedService<GoogleCalendarSyncWorker>();

var host = builder.Build();

await EnsureDatabaseMigrated(host.Services);
await host.RunAsync();

static async Task EnsureDatabaseMigrated(IServiceProvider serviceProvider)
{
    await using var scope = serviceProvider.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BotDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await EnsureGoogleCalendarTables(db);
    await EnsureExceptionLogsTable(db);
}

static async Task EnsureGoogleCalendarTables(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS tenant_google_calendar_config
    (
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
    """);

    await EnsureColumnAsync(db, "tenant_google_calendar_config", "availability_window_days", "INTEGER NOT NULL DEFAULT 7");
    await EnsureColumnAsync(db, "tenant_google_calendar_config", "availability_slot_interval_minutes", "INTEGER NOT NULL DEFAULT 60");
    await EnsureColumnAsync(db, "tenant_google_calendar_config", "availability_workday_start_hour", "INTEGER NOT NULL DEFAULT 8");
    await EnsureColumnAsync(db, "tenant_google_calendar_config", "availability_workday_end_hour", "INTEGER NOT NULL DEFAULT 20");
    await EnsureColumnAsync(db, "tenant_google_calendar_config", "availability_today_lead_minutes", "INTEGER NOT NULL DEFAULT 30");
    await EnsureColumnAsync(db, "tenant_google_calendar_config", "max_attempts", "INTEGER NOT NULL DEFAULT 8");
    await EnsureColumnAsync(db, "tenant_google_calendar_config", "retry_base_seconds", "INTEGER NOT NULL DEFAULT 10");
    await EnsureColumnAsync(db, "tenant_google_calendar_config", "retry_max_seconds", "INTEGER NOT NULL DEFAULT 600");

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS calendar_sync_queue
    (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        tenant_id TEXT NOT NULL,
        job_id INTEGER NOT NULL,
        action TEXT NOT NULL,
        status TEXT NOT NULL,
        attempts INTEGER NOT NULL DEFAULT 0,
        available_at_utc TEXT NOT NULL,
        locked_at_utc TEXT NULL,
        last_error TEXT NULL,
        created_at_utc TEXT NOT NULL,
        updated_at_utc TEXT NOT NULL
    );
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE INDEX IF NOT EXISTS ix_calendar_sync_queue_status_available
    ON calendar_sync_queue (status, available_at_utc, id);
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE INDEX IF NOT EXISTS ix_calendar_sync_queue_tenant_job
    ON calendar_sync_queue (tenant_id, job_id, id);
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS job_calendar_links
    (
        job_id INTEGER PRIMARY KEY,
        tenant_id TEXT NOT NULL,
        calendar_event_id TEXT NOT NULL,
        updated_at_utc TEXT NOT NULL
    );
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE INDEX IF NOT EXISTS ix_job_calendar_links_tenant
    ON job_calendar_links (tenant_id, updated_at_utc);
    """);
}

static async Task EnsureColumnAsync(BotDbContext db, string tableName, string columnName, string columnDefinitionSql)
{
    try
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinitionSql};";
        await command.ExecuteNonQueryAsync();
    }
    catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
        // Column already exists.
    }
}

static async Task EnsureExceptionLogsTable(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS exception_logs
    (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        tenant_id TEXT NOT NULL,
        source TEXT NOT NULL,
        exception_type TEXT NOT NULL,
        message TEXT NOT NULL,
        stack_trace TEXT NOT NULL,
        telegram_user_id INTEGER NULL,
        app_user_id INTEGER NULL,
        related_job_id INTEGER NULL,
        context_payload TEXT NULL,
        created_at_utc TEXT NOT NULL
    );
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE INDEX IF NOT EXISTS ix_exception_logs_tenant_created
    ON exception_logs (tenant_id, created_at_utc DESC);
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE INDEX IF NOT EXISTS ix_exception_logs_tg_user_created
    ON exception_logs (telegram_user_id, created_at_utc DESC);
    """);
}

static TimeZoneInfo ResolveTimeZone(string preferredId)
{
    var safeId = string.IsNullOrWhiteSpace(preferredId) ? "America/Sao_Paulo" : preferredId.Trim();

    try
    {
        return TimeZoneInfo.FindSystemTimeZoneById(safeId);
    }
    catch (TimeZoneNotFoundException) when (OperatingSystem.IsWindows() && safeId == "America/Sao_Paulo")
    {
        return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
    }
}

static string ResolveDatabasePath(string? configuredPath)
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
