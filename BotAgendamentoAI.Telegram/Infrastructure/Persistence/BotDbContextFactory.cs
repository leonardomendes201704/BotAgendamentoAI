using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BotAgendamentoAI.Telegram.Infrastructure.Persistence;

public sealed class BotDbContextFactory : IDesignTimeDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext(string[] args)
    {
        var dbPath = ResolveDatabasePath(Environment.GetEnvironmentVariable("BOT_DB_PATH"));
        var builder = new DbContextOptionsBuilder<BotDbContext>();
        builder.UseSqlite($"Data Source={dbPath}");
        return new BotDbContext(builder.Options);
    }

    private static string ResolveDatabasePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "bot.db")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Debug", "net8.0", "data", "bot.db")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bin", "Debug", "net8.0", "data", "bot.db"))
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
}
