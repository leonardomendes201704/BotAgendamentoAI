using System.Text;
using BotAgendamentoAI.Bot;
using BotAgendamentoAI.Data;
using BotAgendamentoAI.Domain;

namespace BotAgendamentoAI;

public static class Program
{
    public static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("Defina OPENAI_API_KEY antes de executar.");
            Console.WriteLine("Exemplo (PowerShell): $env:OPENAI_API_KEY=\"sua_chave\"");
            return;
        }

        var timeZone = ResolveTimeZone("America/Sao_Paulo");
        var dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "bot.db"));

        var repository = new ConversationRepository(dbPath);
        await repository.InitializeAsync();

        IBookingStore bookingStore = new SqliteBookingStore(dbPath);
        var tools = new SecretaryTools(bookingStore);
        var stateManager = new StateManager(timeZone);

        var bot = new SecretaryBot(
            apiKey: apiKey,
            repository: repository,
            tools: tools,
            stateManager: stateManager,
            timeZone: timeZone,
            model: "gpt-4.1-mini");

        Console.WriteLine("Secretaria de Agendamentos (WhatsApp style)");
        Console.WriteLine($"SQLite: {dbPath}");
        Console.WriteLine($"Timezone: {timeZone.Id}");
        Console.WriteLine("Comandos:");
        Console.WriteLine("  /tenant <id>      troca tenant (padrao: A)");
        Console.WriteLine("  /from <phone>     troca telefone (padrao: 5511999999999)");
        Console.WriteLine("  /history          mostra qtd de mensagens persistidas");
        Console.WriteLine("  /pool             mostra janela atual de pooling (segundos)");
        Console.WriteLine("  /help             ajuda");
        Console.WriteLine("  /exit             sair");

        var tenant = "A";
        var from = "5511999999999";
        var consoleSync = new object();

        await using var dispatcher = new MessagePoolingDispatcher(
            repository,
            bot,
            result =>
            {
                lock (consoleSync)
                {
                    Console.WriteLine();
                    Console.WriteLine($"[{result.TenantId} | {result.Phone}] Bot: {result.BotText}");
                    Console.Write($"\n[{tenant} | {from}] Voce: ");
                }
            });

        while (true)
        {
            lock (consoleSync)
            {
                Console.Write($"\n[{tenant} | {from}] Voce: ");
            }

            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                continue;
            }

            if (line.StartsWith("/tenant ", StringComparison.OrdinalIgnoreCase))
            {
                tenant = line.Substring(8).Trim();
                if (string.IsNullOrWhiteSpace(tenant))
                {
                    tenant = "A";
                }

                Console.WriteLine($"tenant = {tenant}");
                continue;
            }

            if (line.StartsWith("/from ", StringComparison.OrdinalIgnoreCase))
            {
                from = line.Substring(6).Trim();
                if (string.IsNullOrWhiteSpace(from))
                {
                    from = "5511999999999";
                }

                Console.WriteLine($"from = {from}");
                continue;
            }

            if (line.StartsWith("/history", StringComparison.OrdinalIgnoreCase))
            {
                var fullHistory = await repository.GetFullHistory(tenant, from);
                var last24h = await repository.GetLast24h(tenant, from, 40, DateTimeOffset.UtcNow);
                Console.WriteLine($"Mensagens total: {fullHistory.Count} | ultimas 24h: {last24h.Count}");
                continue;
            }

            if (line.StartsWith("/pool", StringComparison.OrdinalIgnoreCase))
            {
                var config = await repository.GetBotTextConfig(tenant);
                Console.WriteLine($"Pooling atual: {config.MessagePoolingSeconds}s (tenant {tenant})");
                continue;
            }

            try
            {
                var poolingSeconds = await dispatcher.EnqueueAsync(tenant, from, line);
                if (poolingSeconds > 0)
                {
                    Console.WriteLine($"[pool] mensagem recebida, aguardando ate {poolingSeconds}s para consolidar contexto.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao enfileirar mensagem: {ex.Message}");
            }
        }

        await dispatcher.FlushAllAsync();
        Console.WriteLine("Encerrado.");
    }

    private static TimeZoneInfo ResolveTimeZone(string preferredId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(preferredId);
        }
        catch (TimeZoneNotFoundException) when (OperatingSystem.IsWindows() && preferredId == "America/Sao_Paulo")
        {
            return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Exemplos:");
        Console.WriteLine("  oi");
        Console.WriteLine("  1 (Agendar Servico)");
        Console.WriteLine("  Meu ar condicionado esta com erro CH26");
        Console.WriteLine("  amanha 09:00");
        Console.WriteLine("  11704150");
        Console.WriteLine("  136 apto 34");
        Console.WriteLine("  2 (Consultar) / 3 (Cancelar) / 4 (Alterar) por numero da lista");
        Console.WriteLine("Pooling:");
        Console.WriteLine("  mensagens enviadas em sequencia curta sao agrupadas (janela configuravel no Admin)");
    }
}
