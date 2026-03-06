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

        if (ShouldReleaseToMenu(incoming.Text))
        {
            Stop(session, UserContextService.HomeStateForRole(sender.Role));
            await db.SaveChangesAsync(cancellationToken);
            return false;
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

        if (incoming.Video is not null)
        {
            await _sender.SendTextAsync(
                db,
                bot,
                tenantId,
                sender.TelegramUserId,
                incoming.Chat.Id,
                "Video nao e suportado no chat. Envie apenas fotos, texto ou localizacao.",
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

    private static bool ShouldReleaseToMenu(string? text)
    {
        var safe = (text ?? string.Empty).Trim();
        if (safe.Length == 0)
        {
            return false;
        }

        if (string.Equals(safe, "/menu", StringComparison.OrdinalIgnoreCase)
            || string.Equals(safe, "menu", StringComparison.OrdinalIgnoreCase)
            || string.Equals(safe, MenuTexts.Back, StringComparison.OrdinalIgnoreCase)
            || string.Equals(safe, MenuTexts.Cancel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (safe is "1" or "2" or "3" or "4" or "5" or "6")
        {
            return true;
        }

        return string.Equals(safe, MenuTexts.ClientRequestService, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ClientMyBookings, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ClientFavorites, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ClientHelp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ClientSwitchToProvider, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ProviderAvailableJobs, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ProviderAgenda, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ProviderProfile, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ProviderPortfolio, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ProviderSettings, StringComparison.OrdinalIgnoreCase)
               || string.Equals(safe, MenuTexts.ProviderSwitchToClient, StringComparison.OrdinalIgnoreCase);
    }
}
