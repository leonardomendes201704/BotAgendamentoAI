using BotAgendamentoAI.Bot;
using BotAgendamentoAI.Data;
using BotAgendamentoAI.Domain;
using BotAgendamentoAI.Telegram;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<TelegramWorkerOptions>(builder.Configuration.GetSection("TelegramWorker"));

var rawOptions = builder.Configuration.GetSection("TelegramWorker").Get<TelegramWorkerOptions>() ?? new TelegramWorkerOptions();
var databasePath = ResolveDatabasePath(rawOptions.DatabasePath);
var timeZone = ResolveTimeZone(rawOptions.TimeZoneId);
var model = string.IsNullOrWhiteSpace(rawOptions.OpenAiModel) ? "gpt-4.1-mini" : rawOptions.OpenAiModel.Trim();
var bootstrapRepository = new ConversationRepository(databasePath);
await bootstrapRepository.InitializeAsync();
var apiKey = await bootstrapRepository.GetOpenAiApiKey();
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Chave OpenAI nao configurada no banco.");
    Console.WriteLine("Configure em Admin > Menu e mensagens > OpenAI API Key.");
    return;
}

builder.Services.AddSingleton(new TelegramRuntimeSettings
{
    DatabasePath = databasePath,
    TimeZone = timeZone,
    OpenAiModel = model,
    TenantIdleDelaySeconds = Math.Clamp(rawOptions.TenantIdleDelaySeconds, 1, 60)
});

builder.Services.AddSingleton<ConversationRepository>(sp =>
{
    return bootstrapRepository;
});
builder.Services.AddSingleton<IBookingStore>(sp =>
{
    var runtime = sp.GetRequiredService<TelegramRuntimeSettings>();
    return new SqliteBookingStore(runtime.DatabasePath);
});
builder.Services.AddSingleton<SecretaryTools>();
builder.Services.AddSingleton<StateManager>(sp =>
{
    var runtime = sp.GetRequiredService<TelegramRuntimeSettings>();
    return new StateManager(runtime.TimeZone);
});
builder.Services.AddSingleton<SecretaryBot>(sp =>
{
    var runtime = sp.GetRequiredService<TelegramRuntimeSettings>();
    return new SecretaryBot(
        apiKey: apiKey,
        repository: sp.GetRequiredService<ConversationRepository>(),
        tools: sp.GetRequiredService<SecretaryTools>(),
        stateManager: sp.GetRequiredService<StateManager>(),
        timeZone: runtime.TimeZone,
        model: runtime.OpenAiModel);
});
builder.Services.AddHttpClient<TelegramApiClient>();
builder.Services.AddHostedService<TelegramPollingWorker>();

var host = builder.Build();
await host.RunAsync();

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
