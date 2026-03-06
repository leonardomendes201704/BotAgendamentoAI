using System.Data;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class BotExceptionLogService
{
    private readonly ILogger<BotExceptionLogService> _logger;

    public BotExceptionLogService(ILogger<BotExceptionLogService> logger)
    {
        _logger = logger;
    }

    public async Task TryLogAsync(
        BotDbContext db,
        string tenantId,
        string source,
        Exception exception,
        long? telegramUserId,
        long? appUserId,
        long? relatedJobId,
        string? contextPayload,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
            """
            INSERT INTO tg_exception_logs
            (
                tenant_id,
                source,
                exception_type,
                message,
                stack_trace,
                telegram_user_id,
                app_user_id,
                related_job_id,
                context_payload,
                created_at_utc
            )
            VALUES
            (
                @tenant_id,
                @source,
                @exception_type,
                @message,
                @stack_trace,
                @telegram_user_id,
                @app_user_id,
                @related_job_id,
                @context_payload,
                @created_at_utc
            );
            """;

            if (db.Database.CurrentTransaction is IDbContextTransaction currentTransaction)
            {
                command.Transaction = currentTransaction.GetDbTransaction();
            }

            AddParameter(command, "@tenant_id", string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim());
            AddParameter(command, "@source", source ?? string.Empty);
            AddParameter(command, "@exception_type", exception.GetType().FullName ?? exception.GetType().Name);
            AddParameter(command, "@message", exception.Message ?? string.Empty);
            AddParameter(command, "@stack_trace", exception.ToString());
            AddParameter(command, "@telegram_user_id", telegramUserId);
            AddParameter(command, "@app_user_id", appUserId);
            AddParameter(command, "@related_job_id", relatedJobId);
            AddParameter(command, "@context_payload", contextPayload ?? string.Empty);
            AddParameter(command, "@created_at_utc", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception logException)
        {
            _logger.LogError(
                logException,
                "Falha ao persistir exception em tg_exception_logs. source={Source} tenant={Tenant}",
                source,
                tenantId);
        }
    }

    private static void AddParameter(IDbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}


