using System.Text.Json;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class HumanHandoffService
{
    private const string DefaultQueueText = "Recebi sua solicitacao. Um atendente humano vai falar com voce em instantes.";
    private const string DefaultActiveText = "Seu atendimento humano esta ativo. Nossa equipe vai responder por aqui.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<HandoffRequestResult> RequestAsync(
        BotDbContext db,
        string tenantId,
        AppUser user,
        CancellationToken cancellationToken)
    {
        var tenant = NormalizeTenant(tenantId);
        var nowUtc = DateTimeOffset.UtcNow;

        var openSession = await GetOpenSessionAsync(db, tenant, user.TelegramUserId, cancellationToken);
        if (openSession is not null)
        {
            openSession.LastMessageAtUtc = nowUtc;
            ApplyHandoffState(user.Session, nowUtc);
            await db.SaveChangesAsync(cancellationToken);

            return new HandoffRequestResult(
                true,
                await ResolveActiveTextAsync(db, tenant, cancellationToken),
                openSession);
        }

        var previousState = ResolvePreviousState(user.Session, user.Role);
        var created = new HumanHandoffSession
        {
            TenantId = tenant,
            TelegramUserId = user.TelegramUserId,
            AppUserId = user.Id,
            RequestedByRole = ResolveRequestedByRole(user),
            IsOpen = true,
            RequestedAtUtc = nowUtc,
            AcceptedAtUtc = null,
            ClosedAtUtc = null,
            AssignedAgent = null,
            PreviousState = previousState,
            CloseReason = null,
            LastMessageAtUtc = nowUtc
        };

        db.HumanHandoffSessions.Add(created);
        ApplyHandoffState(user.Session, nowUtc);
        await db.SaveChangesAsync(cancellationToken);

        return new HandoffRequestResult(
            false,
            await ResolveQueueTextAsync(db, tenant, cancellationToken),
            created);
    }

    public async Task<HumanHandoffSession?> GetOpenSessionAsync(
        BotDbContext db,
        string tenantId,
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        var tenant = NormalizeTenant(tenantId);
        return await db.HumanHandoffSessions
            .FirstOrDefaultAsync(
                x => x.TenantId == tenant
                     && x.TelegramUserId == telegramUserId
                     && x.IsOpen,
                cancellationToken);
    }

    public async Task<string> ResolveActiveTextAsync(
        BotDbContext db,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var queueText = await ResolveQueueTextAsync(db, tenantId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(queueText))
        {
            return queueText;
        }

        return DefaultActiveText;
    }

    public async Task MarkActivityAsync(
        BotDbContext db,
        HumanHandoffSession session,
        CancellationToken cancellationToken)
    {
        session.LastMessageAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyHandoffState(UserSession? session, DateTimeOffset nowUtc)
    {
        if (session is null)
        {
            return;
        }

        session.State = BotStates.HUMAN_HANDOFF;
        session.IsChatActive = false;
        session.ChatJobId = null;
        session.ChatPeerUserId = null;
        session.UpdatedAt = nowUtc;
    }

    private static string ResolvePreviousState(UserSession? session, UserRole role)
    {
        var current = (session?.State ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(current)
            || string.Equals(current, BotStates.HUMAN_HANDOFF, StringComparison.OrdinalIgnoreCase))
        {
            return UserContextService.HomeStateForRole(role);
        }

        return current;
    }

    private static string ResolveRequestedByRole(AppUser user)
    {
        if (user.Role == UserRole.Client)
        {
            return "client";
        }

        if (user.Role == UserRole.Provider)
        {
            return "provider";
        }

        return user.Session?.State.StartsWith("P_", StringComparison.OrdinalIgnoreCase) == true
            ? "provider"
            : "client";
    }

    private async Task<string> ResolveQueueTextAsync(
        BotDbContext db,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var tenant = NormalizeTenant(tenantId);
        var row = await db.TenantBotConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenant, cancellationToken);

        if (row is null || string.IsNullOrWhiteSpace(row.MessagesJson))
        {
            return DefaultQueueText;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<MessagesConfigStorage>(row.MessagesJson, JsonOptions);
            var configured = payload?.HumanHandoffText?.Trim();
            return string.IsNullOrWhiteSpace(configured) ? DefaultQueueText : configured;
        }
        catch
        {
            return DefaultQueueText;
        }
    }

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

    private sealed class MessagesConfigStorage
    {
        public string HumanHandoffText { get; set; } = string.Empty;
    }
}

public sealed record HandoffRequestResult(
    bool IsAlreadyOpen,
    string ResponseText,
    HumanHandoffSession Session);
