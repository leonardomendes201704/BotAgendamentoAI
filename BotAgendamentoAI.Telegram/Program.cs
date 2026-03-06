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
var defaultConnection = builder.Configuration.GetConnectionString("DefaultConnection");
var useSqlServer = !string.IsNullOrWhiteSpace(defaultConnection);
var databasePath = useSqlServer ? string.Empty : ResolveDatabasePath(rawOptions.DatabasePath);
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
    if (useSqlServer)
    {
        options.UseSqlServer(defaultConnection!.Trim());
    }
    else
    {
        options.UseSqlite($"Data Source={databasePath}");
    }
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
builder.Services.AddSingleton<ProviderReminderSettingsService>();
builder.Services.AddSingleton<JobWorkflowService>();
builder.Services.AddSingleton<IPhotoValidator, StubPhotoValidator>();
builder.Services.AddSingleton<ChatMediatorService>();
builder.Services.AddSingleton<ClientFlowHandler>();
builder.Services.AddSingleton<ProviderFlowHandler>();
builder.Services.AddSingleton<MarketplaceBotOrchestrator>();
builder.Services.AddHostedService<TelegramPollingWorker>();
builder.Services.AddHostedService<GoogleCalendarSyncWorker>();
builder.Services.AddHostedService<ProviderProfileReminderWorker>();

var host = builder.Build();

await EnsureDatabaseMigrated(host.Services, useSqlServer);
await host.RunAsync();

static async Task EnsureDatabaseMigrated(IServiceProvider serviceProvider, bool useSqlServer)
{
    await using var scope = serviceProvider.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BotDbContext>>();
    await using var db = await factory.CreateDbContextAsync();

    if (useSqlServer)
    {
        await db.Database.EnsureCreatedAsync();
        await EnsureCoreTablesSqlServer(db);
        await EnsureSharedTablesSqlServer(db);
        await EnsureGoogleCalendarTablesSqlServer(db);
        await EnsureExceptionLogsTableSqlServer(db);
        await EnsureProviderJobRejectionsTableSqlServer(db);
        return;
    }

    await db.Database.MigrateAsync();
    await EnsureGoogleCalendarTables(db);
    await EnsureExceptionLogsTable(db);
    await EnsureProviderJobRejectionsTable(db);
    await EnsureJobContactColumns(db);
}

static async Task EnsureGoogleCalendarTables(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS tg_tenant_google_calendar_config
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

    await EnsureColumnAsync(db, "tg_tenant_google_calendar_config", "availability_window_days", "INTEGER NOT NULL DEFAULT 7");
    await EnsureColumnAsync(db, "tg_tenant_google_calendar_config", "availability_slot_interval_minutes", "INTEGER NOT NULL DEFAULT 60");
    await EnsureColumnAsync(db, "tg_tenant_google_calendar_config", "availability_workday_start_hour", "INTEGER NOT NULL DEFAULT 8");
    await EnsureColumnAsync(db, "tg_tenant_google_calendar_config", "availability_workday_end_hour", "INTEGER NOT NULL DEFAULT 20");
    await EnsureColumnAsync(db, "tg_tenant_google_calendar_config", "availability_today_lead_minutes", "INTEGER NOT NULL DEFAULT 30");
    await EnsureColumnAsync(db, "tg_tenant_google_calendar_config", "max_attempts", "INTEGER NOT NULL DEFAULT 8");
    await EnsureColumnAsync(db, "tg_tenant_google_calendar_config", "retry_base_seconds", "INTEGER NOT NULL DEFAULT 10");
    await EnsureColumnAsync(db, "tg_tenant_google_calendar_config", "retry_max_seconds", "INTEGER NOT NULL DEFAULT 600");

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS tg_calendar_sync_queue
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
    ON tg_calendar_sync_queue (status, available_at_utc, id);
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE INDEX IF NOT EXISTS ix_calendar_sync_queue_tenant_job
    ON tg_calendar_sync_queue (tenant_id, job_id, id);
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS tg_job_calendar_links
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
    ON tg_job_calendar_links (tenant_id, updated_at_utc);
    """);
}

static async Task EnsureCoreTablesSqlServer(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_Users', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_Users
        (
            Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tg_Users PRIMARY KEY,
            TenantId NVARCHAR(32) NOT NULL,
            TelegramUserId BIGINT NOT NULL,
            Name NVARCHAR(120) NOT NULL,
            Username NVARCHAR(120) NULL,
            Phone NVARCHAR(32) NULL,
            Role NVARCHAR(32) NOT NULL,
            IsActive BIT NOT NULL CONSTRAINT DF_tg_Users_IsActive DEFAULT(1),
            CreatedAt DATETIMEOFFSET NOT NULL,
            UpdatedAt DATETIMEOFFSET NOT NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_Users_TenantId_TelegramUserId' AND object_id = OBJECT_ID(N'dbo.tg_Users'))
    BEGIN
        CREATE UNIQUE INDEX IX_tg_Users_TenantId_TelegramUserId
        ON dbo.tg_Users (TenantId, TelegramUserId);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_Users_TenantId_Role' AND object_id = OBJECT_ID(N'dbo.tg_Users'))
    BEGIN
        CREATE INDEX IX_tg_Users_TenantId_Role
        ON dbo.tg_Users (TenantId, Role);
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_ProvidersProfile', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_ProvidersProfile
        (
            UserId BIGINT NOT NULL CONSTRAINT PK_tg_ProvidersProfile PRIMARY KEY,
            Bio NVARCHAR(2048) NOT NULL CONSTRAINT DF_tg_ProvidersProfile_Bio DEFAULT(N''),
            CategoriesJson NVARCHAR(MAX) NOT NULL,
            RadiusKm INT NOT NULL,
            AvgRating DECIMAL(5,2) NOT NULL CONSTRAINT DF_tg_ProvidersProfile_AvgRating DEFAULT(0),
            TotalReviews INT NOT NULL CONSTRAINT DF_tg_ProvidersProfile_TotalReviews DEFAULT(0),
            IsAvailable BIT NOT NULL CONSTRAINT DF_tg_ProvidersProfile_IsAvailable DEFAULT(1),
            BaseLatitude FLOAT NULL,
            BaseLongitude FLOAT NULL,
            CONSTRAINT FK_tg_ProvidersProfile_tg_Users_UserId
                FOREIGN KEY (UserId) REFERENCES dbo.tg_Users (Id) ON DELETE CASCADE
        );
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_ProviderPortfolioPhotos', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_ProviderPortfolioPhotos
        (
            Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tg_ProviderPortfolioPhotos PRIMARY KEY,
            ProviderUserId BIGINT NOT NULL,
            FileIdOrUrl NVARCHAR(1024) NOT NULL,
            CreatedAt DATETIMEOFFSET NOT NULL,
            CONSTRAINT FK_tg_ProviderPortfolioPhotos_tg_Users_ProviderUserId
                FOREIGN KEY (ProviderUserId) REFERENCES dbo.tg_Users (Id) ON DELETE CASCADE
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_ProviderPortfolioPhotos_ProviderUserId_CreatedAt' AND object_id = OBJECT_ID(N'dbo.tg_ProviderPortfolioPhotos'))
    BEGIN
        CREATE INDEX IX_tg_ProviderPortfolioPhotos_ProviderUserId_CreatedAt
        ON dbo.tg_ProviderPortfolioPhotos (ProviderUserId, CreatedAt);
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_Jobs', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_Jobs
        (
            Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tg_Jobs PRIMARY KEY,
            TenantId NVARCHAR(32) NOT NULL,
            ClientUserId BIGINT NOT NULL,
            ProviderUserId BIGINT NULL,
            Category NVARCHAR(128) NOT NULL,
            Description NVARCHAR(MAX) NOT NULL,
            Status NVARCHAR(32) NOT NULL,
            ScheduledAt DATETIMEOFFSET NULL,
            IsUrgent BIT NOT NULL,
            AddressText NVARCHAR(2048) NULL,
            Latitude FLOAT NULL,
            Longitude FLOAT NULL,
            PreferenceCode NVARCHAR(64) NULL,
            ContactName NVARCHAR(120) NULL,
            ContactPhone NVARCHAR(32) NULL,
            FinalAmount DECIMAL(10,2) NULL,
            FinalNotes NVARCHAR(2048) NULL,
            CreatedAt DATETIMEOFFSET NOT NULL,
            UpdatedAt DATETIMEOFFSET NOT NULL,
            CONSTRAINT FK_tg_Jobs_tg_Users_ClientUserId
                FOREIGN KEY (ClientUserId) REFERENCES dbo.tg_Users (Id) ON DELETE NO ACTION,
            CONSTRAINT FK_tg_Jobs_tg_Users_ProviderUserId
                FOREIGN KEY (ProviderUserId) REFERENCES dbo.tg_Users (Id) ON DELETE SET NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_Jobs_TenantId_Status_CreatedAt' AND object_id = OBJECT_ID(N'dbo.tg_Jobs'))
    BEGIN
        CREATE INDEX IX_tg_Jobs_TenantId_Status_CreatedAt
        ON dbo.tg_Jobs (TenantId, Status, CreatedAt);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_Jobs_ClientUserId_CreatedAt' AND object_id = OBJECT_ID(N'dbo.tg_Jobs'))
    BEGIN
        CREATE INDEX IX_tg_Jobs_ClientUserId_CreatedAt
        ON dbo.tg_Jobs (ClientUserId, CreatedAt);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_Jobs_ProviderUserId_Status_UpdatedAt' AND object_id = OBJECT_ID(N'dbo.tg_Jobs'))
    BEGIN
        CREATE INDEX IX_tg_Jobs_ProviderUserId_Status_UpdatedAt
        ON dbo.tg_Jobs (ProviderUserId, Status, UpdatedAt);
    END;
    """);

    await EnsureColumnSqlServer(db, "tg_Jobs", "ContactName", "NVARCHAR(120) NULL");
    await EnsureColumnSqlServer(db, "tg_Jobs", "ContactPhone", "NVARCHAR(32) NULL");

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_JobPhotos', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_JobPhotos
        (
            Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tg_JobPhotos PRIMARY KEY,
            JobId BIGINT NOT NULL,
            TelegramFileId NVARCHAR(512) NOT NULL,
            Kind NVARCHAR(32) NOT NULL,
            CreatedAt DATETIMEOFFSET NOT NULL,
            CONSTRAINT FK_tg_JobPhotos_tg_Jobs_JobId
                FOREIGN KEY (JobId) REFERENCES dbo.tg_Jobs (Id) ON DELETE CASCADE
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_JobPhotos_JobId_CreatedAt' AND object_id = OBJECT_ID(N'dbo.tg_JobPhotos'))
    BEGIN
        CREATE INDEX IX_tg_JobPhotos_JobId_CreatedAt
        ON dbo.tg_JobPhotos (JobId, CreatedAt);
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_MessagesLog', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_MessagesLog
        (
            Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tg_MessagesLog PRIMARY KEY,
            TenantId NVARCHAR(32) NOT NULL,
            TelegramUserId BIGINT NOT NULL,
            Direction NVARCHAR(16) NOT NULL,
            MessageType NVARCHAR(32) NOT NULL,
            Text NVARCHAR(MAX) NOT NULL,
            TelegramMessageId BIGINT NULL,
            RelatedJobId BIGINT NULL,
            CreatedAt DATETIMEOFFSET NOT NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_MessagesLog_TenantId_TelegramUserId_CreatedAt' AND object_id = OBJECT_ID(N'dbo.tg_MessagesLog'))
    BEGIN
        CREATE INDEX IX_tg_MessagesLog_TenantId_TelegramUserId_CreatedAt
        ON dbo.tg_MessagesLog (TenantId, TelegramUserId, CreatedAt);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_MessagesLog_RelatedJobId_CreatedAt' AND object_id = OBJECT_ID(N'dbo.tg_MessagesLog'))
    BEGIN
        CREATE INDEX IX_tg_MessagesLog_RelatedJobId_CreatedAt
        ON dbo.tg_MessagesLog (RelatedJobId, CreatedAt);
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_Ratings', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_Ratings
        (
            Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tg_Ratings PRIMARY KEY,
            JobId BIGINT NOT NULL,
            ClientUserId BIGINT NOT NULL,
            ProviderUserId BIGINT NOT NULL,
            Stars INT NOT NULL,
            Comment NVARCHAR(1024) NULL,
            CreatedAt DATETIMEOFFSET NOT NULL,
            CONSTRAINT FK_tg_Ratings_tg_Jobs_JobId
                FOREIGN KEY (JobId) REFERENCES dbo.tg_Jobs (Id) ON DELETE CASCADE
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_Ratings_JobId' AND object_id = OBJECT_ID(N'dbo.tg_Ratings'))
    BEGIN
        CREATE UNIQUE INDEX IX_tg_Ratings_JobId
        ON dbo.tg_Ratings (JobId);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_Ratings_ProviderUserId_CreatedAt' AND object_id = OBJECT_ID(N'dbo.tg_Ratings'))
    BEGIN
        CREATE INDEX IX_tg_Ratings_ProviderUserId_CreatedAt
        ON dbo.tg_Ratings (ProviderUserId, CreatedAt);
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_UserSessions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_UserSessions
        (
            UserId BIGINT NOT NULL CONSTRAINT PK_tg_UserSessions PRIMARY KEY,
            State NVARCHAR(64) NOT NULL,
            DraftJson NVARCHAR(MAX) NOT NULL,
            ActiveJobId BIGINT NULL,
            ChatJobId BIGINT NULL,
            ChatPeerUserId BIGINT NULL,
            IsChatActive BIT NOT NULL CONSTRAINT DF_tg_UserSessions_IsChatActive DEFAULT(0),
            UpdatedAt DATETIMEOFFSET NOT NULL,
            CONSTRAINT FK_tg_UserSessions_tg_Users_UserId
                FOREIGN KEY (UserId) REFERENCES dbo.tg_Users (Id) ON DELETE CASCADE
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_tg_UserSessions_ActiveJobId' AND object_id = OBJECT_ID(N'dbo.tg_UserSessions'))
    BEGIN
        CREATE INDEX IX_tg_UserSessions_ActiveJobId
        ON dbo.tg_UserSessions (ActiveJobId);
    END;
    """);
}

static async Task EnsureSharedTablesSqlServer(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_service_categories', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_service_categories
        (
            id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tg_service_categories PRIMARY KEY,
            tenant_id NVARCHAR(32) NOT NULL,
            name NVARCHAR(128) NOT NULL,
            normalized_name NVARCHAR(128) NOT NULL,
            created_at_utc NVARCHAR(64) NOT NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_tg_service_categories_tenant_normalized' AND object_id = OBJECT_ID(N'dbo.tg_service_categories'))
    BEGIN
        CREATE UNIQUE INDEX UQ_tg_service_categories_tenant_normalized
        ON dbo.tg_service_categories (tenant_id, normalized_name);
    END;

    IF OBJECT_ID(N'dbo.tg_tenant_telegram_config', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_tenant_telegram_config
        (
            tenant_id NVARCHAR(32) NOT NULL CONSTRAINT PK_tg_tenant_telegram_config PRIMARY KEY,
            bot_id NVARCHAR(128) NOT NULL,
            bot_username NVARCHAR(128) NOT NULL,
            bot_token NVARCHAR(512) NOT NULL,
            is_active BIT NOT NULL CONSTRAINT DF_tg_tenant_telegram_config_is_active DEFAULT(0),
            polling_timeout_seconds INT NOT NULL CONSTRAINT DF_tg_tenant_telegram_config_polling_timeout_seconds DEFAULT(30),
            last_update_id BIGINT NOT NULL CONSTRAINT DF_tg_tenant_telegram_config_last_update_id DEFAULT(0),
            updated_at_utc NVARCHAR(64) NOT NULL
        );
    END;

    IF OBJECT_ID(N'dbo.tg_tenant_bot_config', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_tenant_bot_config
        (
            tenant_id NVARCHAR(32) NOT NULL CONSTRAINT PK_tg_tenant_bot_config PRIMARY KEY,
            menu_json NVARCHAR(MAX) NOT NULL,
            messages_json NVARCHAR(MAX) NOT NULL,
            updated_at_utc NVARCHAR(64) NOT NULL
        );
    END;

    IF OBJECT_ID(N'dbo.tg_shared_settings', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_shared_settings
        (
            setting_key NVARCHAR(128) NOT NULL CONSTRAINT PK_tg_shared_settings PRIMARY KEY,
            setting_value NVARCHAR(MAX) NOT NULL,
            updated_at_utc NVARCHAR(64) NOT NULL
        );
    END;
    """);
}

static async Task EnsureGoogleCalendarTablesSqlServer(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_tenant_google_calendar_config', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_tenant_google_calendar_config
        (
            tenant_id NVARCHAR(32) NOT NULL CONSTRAINT PK_tg_tenant_google_calendar_config PRIMARY KEY,
            is_enabled BIT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_is_enabled DEFAULT(0),
            calendar_id NVARCHAR(512) NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_calendar_id DEFAULT(N''),
            service_account_json NVARCHAR(MAX) NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_service_account_json DEFAULT(N''),
            time_zone_id NVARCHAR(64) NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_time_zone_id DEFAULT(N'America/Sao_Paulo'),
            default_duration_minutes INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_default_duration_minutes DEFAULT(60),
            availability_window_days INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_window_days DEFAULT(7),
            availability_slot_interval_minutes INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_slot_interval_minutes DEFAULT(60),
            availability_workday_start_hour INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_workday_start_hour DEFAULT(8),
            availability_workday_end_hour INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_workday_end_hour DEFAULT(20),
            availability_today_lead_minutes INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_today_lead_minutes DEFAULT(30),
            max_attempts INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_max_attempts DEFAULT(8),
            retry_base_seconds INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_retry_base_seconds DEFAULT(10),
            retry_max_seconds INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_retry_max_seconds DEFAULT(600),
            event_title_template NVARCHAR(MAX) NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_event_title_template DEFAULT(N''),
            event_description_template NVARCHAR(MAX) NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_event_description_template DEFAULT(N''),
            updated_at_utc NVARCHAR(64) NOT NULL
        );
    END;
    """);

    await EnsureColumnSqlServer(
        db,
        "tg_tenant_google_calendar_config",
        "availability_window_days",
        "INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_window_days2 DEFAULT(7) WITH VALUES");
    await EnsureColumnSqlServer(
        db,
        "tg_tenant_google_calendar_config",
        "availability_slot_interval_minutes",
        "INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_slot_interval_minutes2 DEFAULT(60) WITH VALUES");
    await EnsureColumnSqlServer(
        db,
        "tg_tenant_google_calendar_config",
        "availability_workday_start_hour",
        "INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_workday_start_hour2 DEFAULT(8) WITH VALUES");
    await EnsureColumnSqlServer(
        db,
        "tg_tenant_google_calendar_config",
        "availability_workday_end_hour",
        "INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_workday_end_hour2 DEFAULT(20) WITH VALUES");
    await EnsureColumnSqlServer(
        db,
        "tg_tenant_google_calendar_config",
        "availability_today_lead_minutes",
        "INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_availability_today_lead_minutes2 DEFAULT(30) WITH VALUES");
    await EnsureColumnSqlServer(
        db,
        "tg_tenant_google_calendar_config",
        "max_attempts",
        "INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_max_attempts2 DEFAULT(8) WITH VALUES");
    await EnsureColumnSqlServer(
        db,
        "tg_tenant_google_calendar_config",
        "retry_base_seconds",
        "INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_retry_base_seconds2 DEFAULT(10) WITH VALUES");
    await EnsureColumnSqlServer(
        db,
        "tg_tenant_google_calendar_config",
        "retry_max_seconds",
        "INT NOT NULL CONSTRAINT DF_tg_tenant_google_calendar_config_retry_max_seconds2 DEFAULT(600) WITH VALUES");

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_calendar_sync_queue', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_calendar_sync_queue
        (
            id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tg_calendar_sync_queue PRIMARY KEY,
            tenant_id NVARCHAR(32) NOT NULL,
            job_id BIGINT NOT NULL,
            action NVARCHAR(32) NOT NULL,
            status NVARCHAR(32) NOT NULL,
            attempts INT NOT NULL CONSTRAINT DF_tg_calendar_sync_queue_attempts DEFAULT(0),
            available_at_utc NVARCHAR(64) NOT NULL,
            locked_at_utc NVARCHAR(64) NULL,
            last_error NVARCHAR(MAX) NULL,
            created_at_utc NVARCHAR(64) NOT NULL,
            updated_at_utc NVARCHAR(64) NOT NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_calendar_sync_queue_status_available' AND object_id = OBJECT_ID(N'dbo.tg_calendar_sync_queue'))
    BEGIN
        CREATE INDEX ix_calendar_sync_queue_status_available
        ON dbo.tg_calendar_sync_queue (status, available_at_utc, id);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_calendar_sync_queue_tenant_job' AND object_id = OBJECT_ID(N'dbo.tg_calendar_sync_queue'))
    BEGIN
        CREATE INDEX ix_calendar_sync_queue_tenant_job
        ON dbo.tg_calendar_sync_queue (tenant_id, job_id, id);
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_job_calendar_links', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_job_calendar_links
        (
            job_id BIGINT NOT NULL CONSTRAINT PK_tg_job_calendar_links PRIMARY KEY,
            tenant_id NVARCHAR(32) NOT NULL,
            calendar_event_id NVARCHAR(512) NOT NULL,
            updated_at_utc NVARCHAR(64) NOT NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_job_calendar_links_tenant' AND object_id = OBJECT_ID(N'dbo.tg_job_calendar_links'))
    BEGIN
        CREATE INDEX ix_job_calendar_links_tenant
        ON dbo.tg_job_calendar_links (tenant_id, updated_at_utc);
    END;
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

static async Task EnsureColumnSqlServer(BotDbContext db, string tableName, string columnName, string columnDefinitionSql)
{
    await db.Database.ExecuteSqlRawAsync(
        $"""
        IF OBJECT_ID(N'dbo.{tableName}', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.{tableName}', N'{columnName}') IS NULL
        BEGIN
            ALTER TABLE dbo.{tableName} ADD {columnName} {columnDefinitionSql};
        END;
        """);
}

static async Task EnsureExceptionLogsTable(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS tg_exception_logs
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
    ON tg_exception_logs (tenant_id, created_at_utc DESC);
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE INDEX IF NOT EXISTS ix_exception_logs_tg_user_created
    ON tg_exception_logs (telegram_user_id, created_at_utc DESC);
    """);
}

static async Task EnsureExceptionLogsTableSqlServer(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_exception_logs', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_exception_logs
        (
            id BIGINT IDENTITY(1,1) PRIMARY KEY,
            tenant_id NVARCHAR(32) NOT NULL,
            source NVARCHAR(256) NOT NULL,
            exception_type NVARCHAR(512) NOT NULL,
            message NVARCHAR(MAX) NOT NULL,
            stack_trace NVARCHAR(MAX) NOT NULL,
            telegram_user_id BIGINT NULL,
            app_user_id BIGINT NULL,
            related_job_id BIGINT NULL,
            context_payload NVARCHAR(MAX) NULL,
            created_at_utc NVARCHAR(64) NOT NULL
        );
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_exception_logs_tenant_created' AND object_id = OBJECT_ID(N'dbo.tg_exception_logs'))
    BEGIN
        CREATE INDEX ix_exception_logs_tenant_created
        ON dbo.tg_exception_logs (tenant_id, created_at_utc);
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_exception_logs_tg_user_created' AND object_id = OBJECT_ID(N'dbo.tg_exception_logs'))
    BEGIN
        CREATE INDEX ix_exception_logs_tg_user_created
    ON dbo.tg_exception_logs (telegram_user_id, created_at_utc);
    END;
    """);
}

static async Task EnsureProviderJobRejectionsTable(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE TABLE IF NOT EXISTS tg_provider_job_rejections
    (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        tenant_id TEXT NOT NULL,
        job_id INTEGER NOT NULL,
        provider_user_id INTEGER NOT NULL,
        created_at_utc TEXT NOT NULL
    );
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE UNIQUE INDEX IF NOT EXISTS uq_tg_provider_job_rejections_tenant_job_provider
    ON tg_provider_job_rejections (tenant_id, job_id, provider_user_id);
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    CREATE INDEX IF NOT EXISTS ix_tg_provider_job_rejections_tenant_created
    ON tg_provider_job_rejections (tenant_id, created_at_utc);
    """);
}

static async Task EnsureProviderJobRejectionsTableSqlServer(BotDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
    """
    IF OBJECT_ID(N'dbo.tg_provider_job_rejections', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.tg_provider_job_rejections
        (
            id BIGINT IDENTITY(1,1) PRIMARY KEY,
            tenant_id NVARCHAR(32) NOT NULL,
            job_id BIGINT NOT NULL,
            provider_user_id BIGINT NOT NULL,
            created_at_utc NVARCHAR(64) NOT NULL
        );
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'uq_tg_provider_job_rejections_tenant_job_provider' AND object_id = OBJECT_ID(N'dbo.tg_provider_job_rejections'))
    BEGIN
        CREATE UNIQUE INDEX uq_tg_provider_job_rejections_tenant_job_provider
        ON dbo.tg_provider_job_rejections (tenant_id, job_id, provider_user_id);
    END;
    """);

    await db.Database.ExecuteSqlRawAsync(
    """
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tg_provider_job_rejections_tenant_created' AND object_id = OBJECT_ID(N'dbo.tg_provider_job_rejections'))
    BEGIN
        CREATE INDEX ix_tg_provider_job_rejections_tenant_created
        ON dbo.tg_provider_job_rejections (tenant_id, created_at_utc);
    END;
    """);
}

static async Task EnsureJobContactColumns(BotDbContext db)
{
    await EnsureColumnAsync(db, "tg_Jobs", "ContactName", "TEXT NULL");
    await EnsureColumnAsync(db, "tg_Jobs", "ContactPhone", "TEXT NULL");
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

