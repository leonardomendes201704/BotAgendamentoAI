using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class ConversationHistoryService
{
    public async Task LogInboundAsync(
        BotDbContext db,
        string tenantId,
        long telegramUserId,
        MessageType messageType,
        string text,
        long? telegramMessageId,
        long? relatedJobId,
        CancellationToken cancellationToken)
    {
        var row = new MessageLog
        {
            TenantId = NormalizeTenant(tenantId),
            TelegramUserId = telegramUserId,
            Direction = MessageDirection.In,
            MessageType = messageType,
            Text = text ?? string.Empty,
            TelegramMessageId = telegramMessageId,
            RelatedJobId = relatedJobId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.MessagesLog.Add(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task LogOutboundAsync(
        BotDbContext db,
        string tenantId,
        long telegramUserId,
        MessageType messageType,
        string text,
        long? telegramMessageId,
        long? relatedJobId,
        CancellationToken cancellationToken)
    {
        var row = new MessageLog
        {
            TenantId = NormalizeTenant(tenantId),
            TelegramUserId = telegramUserId,
            Direction = MessageDirection.Out,
            MessageType = messageType,
            Text = text ?? string.Empty,
            TelegramMessageId = telegramMessageId,
            RelatedJobId = relatedJobId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.MessagesLog.Add(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MessageLog>> LoadContextAsync(
        BotDbContext db,
        string tenantId,
        long telegramUserId,
        long? relatedJobId,
        int limit,
        CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        var tenant = NormalizeTenant(tenantId);

        IQueryable<MessageLog> query;
        if (relatedJobId.HasValue)
        {
            query = db.MessagesLog
                .AsNoTracking()
                .Where(x => x.TenantId == tenant && x.RelatedJobId == relatedJobId.Value)
                .OrderByDescending(x => x.Id)
                .Take(safeLimit);
        }
        else
        {
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            query = db.MessagesLog
                .AsNoTracking()
                .Where(x => x.TenantId == tenant && x.TelegramUserId == telegramUserId && x.CreatedAt >= since)
                .OrderByDescending(x => x.Id)
                .Take(safeLimit);
        }

        var rows = await query.ToListAsync(cancellationToken);
        rows.Reverse();
        return rows;
    }

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();
}
