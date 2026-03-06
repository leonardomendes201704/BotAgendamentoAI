using BotAgendamentoAI.Telegram;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Features.Client;
using BotAgendamentoAI.Telegram.Features.Provider;
using BotAgendamentoAI.Telegram.Features.Shared;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using BotAgendamentoAI.Telegram.Infrastructure.Services;
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
builder.Services.AddSingleton<JobWorkflowService>();
builder.Services.AddSingleton<IPhotoValidator, StubPhotoValidator>();
builder.Services.AddSingleton<ChatMediatorService>();
builder.Services.AddSingleton<ClientFlowHandler>();
builder.Services.AddSingleton<ProviderFlowHandler>();
builder.Services.AddSingleton<MarketplaceBotOrchestrator>();
builder.Services.AddHostedService<TelegramPollingWorker>();

var host = builder.Build();

await EnsureDatabaseMigrated(host.Services);
await host.RunAsync();

static async Task EnsureDatabaseMigrated(IServiceProvider serviceProvider)
{
    await using var scope = serviceProvider.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BotDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
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
