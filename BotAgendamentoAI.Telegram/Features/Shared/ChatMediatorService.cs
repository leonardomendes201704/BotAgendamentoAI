using BotAgendamentoAI.Telegram.Application.Common;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BotAgendamentoAI.Telegram.TelegramCompat;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;

namespace BotAgendamentoAI.Telegram.Features.Shared;

public sealed class ChatMediatorService
{
    private readonly TelegramMessageSender _sender;

    public ChatMediatorService(TelegramMessageSender sender)
    {
        _sender = sender;
    }

    public static void Start(UserSession session, long jobId, long peerUserId)
    {
        session.State = BotStates.CHAT_MEDIATED;
        session.IsChatActive = true;
        session.ChatJobId = jobId;
        session.ChatPeerUserId = peerUserId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void Stop(UserSession session, string fallbackState)
    {
        session.IsChatActive = false;
        session.ChatPeerUserId = null;
        session.ChatJobId = null;
        session.State = fallbackState;
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task<bool> TryHandleChatMessageAsync(
        BotDbContext db,
        ITelegramBotClient bot,
        string tenantId,
        AppUser sender,
        Message incoming,
        CancellationToken cancellationToken)
    {
        var session = sender.Session;
        if (session is null || !session.IsChatActive || !session.ChatPeerUserId.HasValue)
        {
            return false;
        }

        if (string.Equals(incoming.Text?.Trim(), "/sairchat", StringComparison.OrdinalIgnoreCase))
        {
            Stop(session, UserContextService.HomeStateForRole(sender.Role));
            await db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                db,
                bot,
                tenantId,
                sender.TelegramUserId,
                incoming.Chat.Id,
                BotMessages.ChatClosed(),
                null,
                session.ActiveJobId,
                cancellationToken);

            return true;
        }

        var peer = await db.Users
            .Include(x => x.Session)
            .FirstOrDefaultAsync(
                x => x.Id == session.ChatPeerUserId.Value && x.TenantId == sender.TenantId,
                cancellationToken);

        if (peer is null)
        {
            Stop(session, UserContextService.HomeStateForRole(sender.Role));
            await db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                db,
                bot,
                tenantId,
                sender.TelegramUserId,
                incoming.Chat.Id,
                "Seu contato de chat nao esta mais disponivel.",
                null,
                session.ActiveJobId,
                cancellationToken);

            return true;
        }

        var prefix = BuildSenderLabel(sender, session.State);
        var peerChatId = new ChatId(peer.TelegramUserId);

        if (!string.IsNullOrWhiteSpace(incoming.Text))
        {
            await _sender.SendTextAsync(
                db,
                bot,
                tenantId,
                peer.TelegramUserId,
                peerChatId,
                $"{prefix}: {incoming.Text}",
                null,
                session.ChatJobId,
                cancellationToken);

            return true;
        }

        if (incoming.Photo?.Length > 0)
        {
            var fileId = incoming.Photo[^1].FileId;
            await _sender.SendPhotoCardAsync(
                db,
                bot,
                tenantId,
                peer.TelegramUserId,
                peerChatId,
                fileId,
                $"{prefix} enviou uma foto",
                null,
                session.ChatJobId,
                cancellationToken);

            return true;
        }

        if (incoming.Location is not null)
        {
            await _sender.SendLocationAsync(
                db,
                bot,
                tenantId,
                peer.TelegramUserId,
                peerChatId,
                incoming.Location.Latitude,
                incoming.Location.Longitude,
                session.ChatJobId,
                cancellationToken);

            await _sender.SendTextAsync(
                db,
                bot,
                tenantId,
                peer.TelegramUserId,
                peerChatId,
                $"{prefix} compartilhou localizacao",
                null,
                session.ChatJobId,
                cancellationToken);

            return true;
        }

        return true;
    }

    private static string BuildSenderLabel(AppUser sender, string sessionState)
    {
        var name = string.IsNullOrWhiteSpace(sender.Name) ? "Usuario" : sender.Name.Trim();

        if (sender.Role == Domain.Enums.UserRole.Provider)
        {
            return $"Prestador {name}";
        }

        if (sender.Role == Domain.Enums.UserRole.Client)
        {
            return $"Cliente {name}";
        }

        return sessionState.StartsWith("P_", StringComparison.OrdinalIgnoreCase)
            ? $"Prestador {name}"
            : $"Cliente {name}";
    }
}
