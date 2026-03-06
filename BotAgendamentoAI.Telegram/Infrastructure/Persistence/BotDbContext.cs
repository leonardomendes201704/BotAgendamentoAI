using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Globalization;

namespace BotAgendamentoAI.Telegram.Infrastructure.Persistence;

public sealed class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ProviderProfile> ProvidersProfile => Set<ProviderProfile>();
    public DbSet<ProviderPortfolioPhoto> ProviderPortfolioPhotos => Set<ProviderPortfolioPhoto>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobPhoto> JobPhotos => Set<JobPhoto>();
    public DbSet<MessageLog> MessagesLog => Set<MessageLog>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<TelegramTenantConfig> TelegramConfigs => Set<TelegramTenantConfig>();
    public DbSet<ServiceCategoryEntity> ServiceCategories => Set<ServiceCategoryEntity>();
    public DbSet<SharedSetting> SharedSettings => Set<SharedSetting>();
    public DbSet<TenantBotConfig> TenantBotConfigs => Set<TenantBotConfig>();
    public DbSet<TenantGoogleCalendarConfig> TenantGoogleCalendarConfigs => Set<TenantGoogleCalendarConfig>();
    public DbSet<CalendarSyncQueueItem> CalendarSyncQueue => Set<CalendarSyncQueueItem>();
    public DbSet<JobCalendarLink> JobCalendarLinks => Set<JobCalendarLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var userRoleConverter = new EnumToStringConverter<UserRole>();
        var jobStatusConverter = new EnumToStringConverter<JobStatus>();
        var directionConverter = new EnumToStringConverter<MessageDirection>();
        var messageTypeConverter = new EnumToStringConverter<MessageType>();
        var dateTimeOffsetTextConverter = new ValueConverter<DateTimeOffset, string>(
            value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        var nullableDateTimeOffsetTextConverter = new ValueConverter<DateTimeOffset?, string?>(
            value => value.HasValue
                ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : null,
            value => string.IsNullOrWhiteSpace(value)
                ? null
                : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("tg_Users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).IsRequired().HasMaxLength(32);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(120);
            entity.Property(x => x.Username).HasMaxLength(120);
            entity.Property(x => x.Phone).HasMaxLength(32);
            entity.Property(x => x.Role).HasConversion(userRoleConverter).HasMaxLength(32);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();

            entity.HasIndex(x => new { x.TenantId, x.TelegramUserId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Role });
        });

        modelBuilder.Entity<ProviderProfile>(entity =>
        {
            entity.ToTable("tg_ProvidersProfile");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.Bio).HasMaxLength(2048);
            entity.Property(x => x.CategoriesJson).IsRequired();
            entity.Property(x => x.RadiusKm).IsRequired();
            entity.Property(x => x.AvgRating).HasPrecision(5, 2);

            entity.HasOne(x => x.User)
                .WithOne(x => x.ProviderProfile)
                .HasForeignKey<ProviderProfile>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderPortfolioPhoto>(entity =>
        {
            entity.ToTable("tg_ProviderPortfolioPhotos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileIdOrUrl).IsRequired().HasMaxLength(1024);
            entity.Property(x => x.CreatedAt).IsRequired();

            entity.HasIndex(x => new { x.ProviderUserId, x.CreatedAt });

            entity.HasOne(x => x.ProviderUser)
                .WithMany()
                .HasForeignKey(x => x.ProviderUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("tg_Jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).IsRequired().HasMaxLength(32);
            entity.Property(x => x.Category).IsRequired().HasMaxLength(128);
            entity.Property(x => x.Description).IsRequired().HasMaxLength(4096);
            entity.Property(x => x.Status).HasConversion(jobStatusConverter).HasMaxLength(32);
            entity.Property(x => x.AddressText).HasMaxLength(2048);
            entity.Property(x => x.PreferenceCode).HasMaxLength(64);
            entity.Property(x => x.ContactName).HasMaxLength(120);
            entity.Property(x => x.ContactPhone).HasMaxLength(32);
            entity.Property(x => x.FinalAmount).HasPrecision(10, 2);
            entity.Property(x => x.FinalNotes).HasMaxLength(2048);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();

            entity.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            entity.HasIndex(x => new { x.ClientUserId, x.CreatedAt });
            entity.HasIndex(x => new { x.ProviderUserId, x.Status, x.UpdatedAt });

            entity.HasOne(x => x.ClientUser)
                .WithMany(x => x.ClientJobs)
                .HasForeignKey(x => x.ClientUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.ProviderUser)
                .WithMany(x => x.ProviderJobs)
                .HasForeignKey(x => x.ProviderUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<JobPhoto>(entity =>
        {
            entity.ToTable("tg_JobPhotos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TelegramFileId).IsRequired().HasMaxLength(512);
            entity.Property(x => x.Kind).IsRequired().HasMaxLength(32);
            entity.Property(x => x.CreatedAt).IsRequired();

            entity.HasIndex(x => new { x.JobId, x.CreatedAt });

            entity.HasOne(x => x.Job)
                .WithMany(x => x.Photos)
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageLog>(entity =>
        {
            entity.ToTable("tg_MessagesLog");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).IsRequired().HasMaxLength(32);
            entity.Property(x => x.Direction).HasConversion(directionConverter).HasMaxLength(16);
            entity.Property(x => x.MessageType).HasConversion(messageTypeConverter).HasMaxLength(32);
            entity.Property(x => x.Text).IsRequired().HasMaxLength(4096);
            entity.Property(x => x.CreatedAt).IsRequired();

            entity.HasIndex(x => new { x.TenantId, x.TelegramUserId, x.CreatedAt });
            entity.HasIndex(x => new { x.RelatedJobId, x.CreatedAt });
        });

        modelBuilder.Entity<Rating>(entity =>
        {
            entity.ToTable("tg_Ratings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Stars).IsRequired();
            entity.Property(x => x.Comment).HasMaxLength(1024);
            entity.Property(x => x.CreatedAt).IsRequired();

            entity.HasIndex(x => x.JobId).IsUnique();
            entity.HasIndex(x => new { x.ProviderUserId, x.CreatedAt });

            entity.HasOne(x => x.Job)
                .WithMany(x => x.Ratings)
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.ToTable("tg_UserSessions");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.State).IsRequired().HasMaxLength(64);
            entity.Property(x => x.DraftJson).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
            entity.HasIndex(x => x.ActiveJobId);

            entity.HasOne(x => x.User)
                .WithOne(x => x.Session)
                .HasForeignKey<UserSession>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceCategoryEntity>(entity =>
        {
            entity.ToTable("tg_service_categories", tableBuilder => tableBuilder.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.NormalizedName).HasColumnName("normalized_name");
            entity.Property(x => x.CreatedAtUtc)
                .HasColumnName("created_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
        });

        modelBuilder.Entity<TelegramTenantConfig>(entity =>
        {
            entity.ToTable("tg_tenant_telegram_config", tableBuilder => tableBuilder.ExcludeFromMigrations());
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.BotId).HasColumnName("bot_id");
            entity.Property(x => x.BotUsername).HasColumnName("bot_username");
            entity.Property(x => x.BotToken).HasColumnName("bot_token");
            entity.Property(x => x.IsActive).HasColumnName("is_active");
            entity.Property(x => x.PollingTimeoutSeconds).HasColumnName("polling_timeout_seconds");
            entity.Property(x => x.LastUpdateId).HasColumnName("last_update_id");
            entity.Property(x => x.UpdatedAtUtc)
                .HasColumnName("updated_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
        });

        modelBuilder.Entity<SharedSetting>(entity =>
        {
            entity.ToTable("tg_shared_settings", tableBuilder => tableBuilder.ExcludeFromMigrations());
            entity.HasKey(x => x.SettingKey);
            entity.Property(x => x.SettingKey).HasColumnName("setting_key");
            entity.Property(x => x.SettingValue).HasColumnName("setting_value");
            entity.Property(x => x.UpdatedAtUtc)
                .HasColumnName("updated_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
        });

        modelBuilder.Entity<TenantBotConfig>(entity =>
        {
            entity.ToTable("tg_tenant_bot_config", tableBuilder => tableBuilder.ExcludeFromMigrations());
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.MenuJson).HasColumnName("menu_json");
            entity.Property(x => x.MessagesJson).HasColumnName("messages_json");
            entity.Property(x => x.UpdatedAtUtc)
                .HasColumnName("updated_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
        });

        modelBuilder.Entity<TenantGoogleCalendarConfig>(entity =>
        {
            entity.ToTable("tg_tenant_google_calendar_config", tableBuilder => tableBuilder.ExcludeFromMigrations());
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.CalendarId).HasColumnName("calendar_id");
            entity.Property(x => x.ServiceAccountJson).HasColumnName("service_account_json");
            entity.Property(x => x.TimeZoneId).HasColumnName("time_zone_id");
            entity.Property(x => x.DefaultDurationMinutes).HasColumnName("default_duration_minutes");
            entity.Property(x => x.AvailabilityWindowDays).HasColumnName("availability_window_days");
            entity.Property(x => x.AvailabilitySlotIntervalMinutes).HasColumnName("availability_slot_interval_minutes");
            entity.Property(x => x.AvailabilityWorkdayStartHour).HasColumnName("availability_workday_start_hour");
            entity.Property(x => x.AvailabilityWorkdayEndHour).HasColumnName("availability_workday_end_hour");
            entity.Property(x => x.AvailabilityTodayLeadMinutes).HasColumnName("availability_today_lead_minutes");
            entity.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
            entity.Property(x => x.RetryBaseSeconds).HasColumnName("retry_base_seconds");
            entity.Property(x => x.RetryMaxSeconds).HasColumnName("retry_max_seconds");
            entity.Property(x => x.EventTitleTemplate).HasColumnName("event_title_template");
            entity.Property(x => x.EventDescriptionTemplate).HasColumnName("event_description_template");
            entity.Property(x => x.UpdatedAtUtc)
                .HasColumnName("updated_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
        });

        modelBuilder.Entity<CalendarSyncQueueItem>(entity =>
        {
            entity.ToTable("tg_calendar_sync_queue", tableBuilder => tableBuilder.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.Action).HasColumnName("action");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.Attempts).HasColumnName("attempts");
            entity.Property(x => x.AvailableAtUtc)
                .HasColumnName("available_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
            entity.Property(x => x.LockedAtUtc)
                .HasColumnName("locked_at_utc")
                .HasMaxLength(64)
                .HasConversion(nullableDateTimeOffsetTextConverter);
            entity.Property(x => x.LastError).HasColumnName("last_error");
            entity.Property(x => x.CreatedAtUtc)
                .HasColumnName("created_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
            entity.Property(x => x.UpdatedAtUtc)
                .HasColumnName("updated_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
        });

        modelBuilder.Entity<JobCalendarLink>(entity =>
        {
            entity.ToTable("tg_job_calendar_links", tableBuilder => tableBuilder.ExcludeFromMigrations());
            entity.HasKey(x => x.JobId);
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.CalendarEventId).HasColumnName("calendar_event_id");
            entity.Property(x => x.UpdatedAtUtc)
                .HasColumnName("updated_at_utc")
                .HasMaxLength(64)
                .HasConversion(dateTimeOffsetTextConverter);
        });

        base.OnModelCreating(modelBuilder);
    }
}
