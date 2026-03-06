using BotAgendamentoAI.Telegram.Application.Callback;
using BotAgendamentoAI.Telegram.Application.Common;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Features.Client;
using BotAgendamentoAI.Telegram.Features.Provider;
using BotAgendamentoAI.Telegram.Features.Shared;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BotAgendamentoAI.Telegram.TelegramCompat;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using System.Globalization;
using System.Text;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class MarketplaceBotOrchestrator
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly UserContextService _userContextService;
    private readonly ConversationHistoryService _historyService;
    private readonly TelegramMessageSender _sender;
    private readonly ClientFlowHandler _clientFlow;
    private readonly ProviderFlowHandler _providerFlow;
    private readonly ChatMediatorService _chatMediator;
    private readonly HumanHandoffService _humanHandoff;
    private readonly ILogger<MarketplaceBotOrchestrator> _logger;

    public MarketplaceBotOrchestrator(
        IDbContextFactory<BotDbContext> dbFactory,
        UserContextService userContextService,
        ConversationHistoryService historyService,
        TelegramMessageSender sender,
        ClientFlowHandler clientFlow,
        ProviderFlowHandler providerFlow,
        ChatMediatorService chatMediator,
        HumanHandoffService humanHandoff,
        ILogger<MarketplaceBotOrchestrator> logger)
    {
        _dbFactory = dbFactory;
        _userContextService = userContextService;
        _historyService = historyService;
        _sender = sender;
        _clientFlow = clientFlow;
        _providerFlow = providerFlow;
        _chatMediator = chatMediator;
        _humanHandoff = humanHandoff;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(
        string tenantId,
        ITelegramBotClient botClient,
        Update update,
        TelegramRuntimeSettings runtime,
        CancellationToken cancellationToken)
    {
        if (update.Message is not null)
        {
            await HandleMessageAsync(tenantId, botClient, update.Message, runtime, cancellationToken);
            return;
        }

        if (update.CallbackQuery is not null)
        {
            await HandleCallbackAsync(tenantId, botClient, update.CallbackQuery, runtime, cancellationToken);
        }
    }

    private async Task HandleMessageAsync(
        string tenantId,
        ITelegramBotClient botClient,
        Message message,
        TelegramRuntimeSettings runtime,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var from = ResolveFromUser(message);
        var userResult = await _userContextService.EnsureUserAsync(db, tenantId, from, cancellationToken);
        var user = userResult.User;

        user.Session ??= new Domain.Entities.UserSession
        {
            UserId = user.Id,
            State = BotStates.NONE,
            DraftJson = "{}",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        if (UserContextService.IsSessionExpired(user.Session, runtime.SessionExpiryMinutes)
            && user.Session.State is not BotStates.C_HOME and not BotStates.P_HOME and not BotStates.NONE and not BotStates.U_ROLE_REQUIRED)
        {
            UserContextService.ResetSession(user.Session, UserContextService.HomeStateForRole(user.Role));
            await db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                db,
                botClient,
                tenantId,
                user.TelegramUserId,
                message.Chat.Id,
                BotMessages.StateExpired,
                user.Role == UserRole.Provider
                    ? KeyboardFactory.ProviderMenu(user.Role == UserRole.Both)
                    : KeyboardFactory.ClientHomeActions(user.Role == UserRole.Both),
                null,
                cancellationToken);
        }

        var messageType = DetectMessageType(message);
        var textToLog = message.Text ?? DescribeNonText(message);
        await _historyService.LogInboundAsync(
            db,
            tenantId,
            user.TelegramUserId,
            messageType,
            textToLog,
            message.MessageId,
            user.Session.ActiveJobId,
            cancellationToken);

        if (await TryHandleHumanHandoffMessageAsync(db, botClient, tenantId, user, message, cancellationToken))
        {
            return;
        }

        if (string.Equals(message.Text?.Trim(), "/start", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsProviderMode(user) && _clientFlow.IsClientRegistrationPending(user))
            {
                var registrationContext = BuildContext(tenantId, db, botClient, user, runtime);
                await _clientFlow.StartOrResumeRegistrationAsync(registrationContext, message.Chat.Id, cancellationToken);
                return;
            }

            await _sender.SendTextAsync(
                db,
                botClient,
                tenantId,
                user.TelegramUserId,
                message.Chat.Id,
                BotMessages.WelcomeRoleChoice(),
                KeyboardFactory.RoleChoice(),
                null,
                cancellationToken);
            return;
        }

        if (userResult.IsNewUser || string.Equals(user.Session.State, BotStates.U_ROLE_REQUIRED, StringComparison.Ordinal))
        {
            await _sender.SendTextAsync(
                db,
                botClient,
                tenantId,
                user.TelegramUserId,
                message.Chat.Id,
                BotMessages.WelcomeRoleChoice(),
                KeyboardFactory.RoleChoice(),
                null,
                cancellationToken);
            return;
        }

        try
        {
            _ = await _historyService.LoadContextAsync(
                db,
                tenantId,
                user.TelegramUserId,
                user.Session.ActiveJobId,
                runtime.HistoryLimitPerContext,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao carregar contexto tenant={Tenant} user={UserId}", tenantId, user.TelegramUserId);
        }

        if (await _chatMediator.TryHandleChatMessageAsync(db, botClient, tenantId, user, message, cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var context = BuildContext(tenantId, db, botClient, user, runtime);

        if (!IsProviderMode(user) && _clientFlow.IsClientRegistrationPending(user))
        {
            if (await _clientFlow.TryHandleRegistrationTextAsync(context, message, cancellationToken))
            {
                return;
            }
        }

        if (message.Location is not null)
        {
            if (string.Equals(user.Session.State, BotStates.P_PROFILE_EDIT, StringComparison.Ordinal))
            {
                await _providerFlow.HandleProviderLocationAsync(context, message, cancellationToken);
                return;
            }

            await _clientFlow.HandleLocationMessageAsync(context, message, cancellationToken);
            return;
        }

        if (message.Photo?.Length > 0)
        {
            if (IsProviderMode(user))
            {
                await _providerFlow.HandlePhotoAsync(context, message, cancellationToken);
            }
            else
            {
                await _clientFlow.HandlePhotoAsync(context, message, cancellationToken);
            }

            return;
        }

        if (!IsProviderMode(user) && message.Video is not null)
        {
            var inPhotoStep = string.Equals(user.Session.State, BotStates.C_COLLECT_PHOTOS, StringComparison.Ordinal);
            await _sender.SendTextAsync(
                db,
                botClient,
                tenantId,
                user.TelegramUserId,
                message.Chat.Id,
                inPhotoStep
                    ? "Video nao e permitido nessa etapa. Envie apenas fotos ou toque em 'Concluir fotos'."
                    : "No momento aceitamos apenas fotos. Video nao e suportado.",
                inPhotoStep
                    ? KeyboardFactory.PhotoCollectMenu()
                    : KeyboardFactory.ClientHomeActions(user.Role == UserRole.Both),
                user.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        if (IsProviderMode(user))
        {
            await _providerFlow.HandleTextAsync(context, message, cancellationToken);
        }
        else
        {
            await _clientFlow.HandleTextAsync(context, message, cancellationToken);
        }
    }

    private async Task HandleCallbackAsync(
        string tenantId,
        ITelegramBotClient botClient,
        CallbackQuery callback,
        TelegramRuntimeSettings runtime,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var from = callback.From ?? new User
        {
            Id = callback.Message?.Chat.Id ?? 0,
            FirstName = "Usuario",
            Username = ""
        };

        var userResult = await _userContextService.EnsureUserAsync(db, tenantId, from, cancellationToken);
        var user = userResult.User;
        user.Session ??= new Domain.Entities.UserSession
        {
            UserId = user.Id,
            State = BotStates.NONE,
            DraftJson = "{}",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var callbackData = callback.Data ?? string.Empty;

        await _historyService.LogInboundAsync(
            db,
            tenantId,
            user.TelegramUserId,
            MessageType.Callback,
            callbackData,
            callback.Message?.MessageId,
            user.Session.ActiveJobId,
            cancellationToken);

        if (!CallbackDataRouter.TryParse(callbackData, out var route))
        {
            await botClient.AnswerCallbackQuery(callback.Id, BotMessages.CallbackExpired, false, cancellationToken);
            return;
        }

        var chatId = callback.Message?.Chat.Id ?? user.TelegramUserId;
        if (await TryHandleHumanHandoffCallbackAsync(db, botClient, tenantId, user, callback, route, chatId, cancellationToken))
        {
            return;
        }

        if (route.Scope == "U" && route.Action == "ROLE")
        {
            await HandleRoleSelectionAsync(db, botClient, tenantId, user, route.Arg1, chatId, runtime, cancellationToken);
            await botClient.AnswerCallbackQuery(callback.Id, "Perfil atualizado", false, cancellationToken);
            return;
        }

        var context = BuildContext(tenantId, db, botClient, user, runtime);

        if (!IsProviderMode(user) && _clientFlow.IsClientRegistrationPending(user))
        {
            if (await _clientFlow.TryHandleRegistrationCallbackAsync(context, route, callback, cancellationToken))
            {
                await botClient.AnswerCallbackQuery(callback.Id, null, false, cancellationToken);
                return;
            }
        }

        if (route.Scope == "P" && route.Action == "REM")
        {
            _ = await _providerFlow.HandleCallbackAsync(context, route, callback, cancellationToken);
            await botClient.AnswerCallbackQuery(callback.Id, null, false, cancellationToken);
            return;
        }

        if (route.Scope == "C" && route.Action == "HOME")
        {
            var handledClientHome = await _clientFlow.HandleCallbackAsync(context, route, callback, cancellationToken);
            if (!handledClientHome)
            {
                await _sender.SendTextAsync(
                    db,
                    botClient,
                    tenantId,
                    user.TelegramUserId,
                    chatId,
                    BotMessages.ClientHomeMenu(user.Role == UserRole.Both),
                    KeyboardFactory.ClientHomeActions(user.Role == UserRole.Both),
                    user.Session.ActiveJobId,
                    cancellationToken);
            }

            await botClient.AnswerCallbackQuery(callback.Id, null, false, cancellationToken);
            return;
        }

        if (route.Scope == "J"
            && long.TryParse(route.Action, out var jobId)
            && route.Arg1 == "CHAT"
            && route.Arg2 != "EXIT")
        {
            if (IsProviderMode(user))
            {
                await _providerFlow.OpenChatAsync(context, jobId, chatId, cancellationToken);
            }
            else
            {
                await _clientFlow.OpenChatAsync(context, jobId, chatId, cancellationToken);
            }

            await botClient.AnswerCallbackQuery(callback.Id, "Chat aberto", false, cancellationToken);
            return;
        }

        var handled = IsProviderMode(user)
            ? await _providerFlow.HandleCallbackAsync(context, route, callback, cancellationToken)
            : await _clientFlow.HandleCallbackAsync(context, route, callback, cancellationToken);

        if (!handled)
        {
            await _sender.SendTextAsync(
                db,
                botClient,
                tenantId,
                user.TelegramUserId,
                chatId,
                BotMessages.UnknownCommand,
                IsProviderMode(user)
                    ? KeyboardFactory.ProviderMenu(user.Role == UserRole.Both)
                    : KeyboardFactory.ClientHomeActions(user.Role == UserRole.Both),
                user.Session.ActiveJobId,
                cancellationToken);
        }

        await botClient.AnswerCallbackQuery(callback.Id, null, false, cancellationToken);
    }

    private async Task HandleRoleSelectionAsync(
        BotDbContext db,
        ITelegramBotClient botClient,
        string tenantId,
        Domain.Entities.AppUser user,
        string code,
        ChatId chatId,
        TelegramRuntimeSettings runtime,
        CancellationToken cancellationToken)
    {
        user.Role = code switch
        {
            "P" => UserRole.Provider,
            "B" => UserRole.Both,
            _ => UserRole.Client
        };

        user.UpdatedAt = DateTimeOffset.UtcNow;

        if (user.Role is UserRole.Provider or UserRole.Both)
        {
            var profile = await db.ProvidersProfile.FirstOrDefaultAsync(x => x.UserId == user.Id, cancellationToken);
            if (profile is null)
            {
                db.ProvidersProfile.Add(new Domain.Entities.ProviderProfile
                {
                    UserId = user.Id,
                    Bio = string.Empty,
                    CategoriesJson = "[]",
                    RadiusKm = 10,
                    AvgRating = 0,
                    TotalReviews = 0,
                    IsAvailable = true
                });
            }
        }

        if (user.Session is not null)
        {
            UserContextService.ResetSession(user.Session, UserContextService.HomeStateForRole(user.Role));
        }

        await db.SaveChangesAsync(cancellationToken);

        if (user.Role == UserRole.Provider)
        {
            await _sender.SendTextAsync(
                db,
                botClient,
                tenantId,
                user.TelegramUserId,
                chatId,
                BotMessages.ProviderHomeMenu(user.Role == UserRole.Both),
                KeyboardFactory.ProviderMenu(user.Role == UserRole.Both),
                null,
                cancellationToken);
        }
        else
        {
            var context = BuildContext(tenantId, db, botClient, user, runtime);
            if (_clientFlow.IsClientRegistrationPending(user))
            {
                await _clientFlow.StartOrResumeRegistrationAsync(context, chatId, cancellationToken);
            }
            else
            {
                await _sender.SendTextAsync(
                    db,
                    botClient,
                    tenantId,
                    user.TelegramUserId,
                    chatId,
                    BotMessages.ClientHomeMenu(user.Role == UserRole.Both),
                    KeyboardFactory.ClientHomeActions(user.Role == UserRole.Both),
                    null,
                    cancellationToken);
            }
        }
    }

    private static User ResolveFromUser(Message message)
    {
        if (message.From is not null)
        {
            return message.From;
        }

        return new User
        {
            Id = message.Chat.Id,
            FirstName = "Usuario",
            Username = string.Empty
        };
    }

    private static MessageType DetectMessageType(Message message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            return message.Text.StartsWith("/", StringComparison.Ordinal) ? MessageType.Command : MessageType.Text;
        }

        if (message.Photo?.Length > 0)
        {
            return MessageType.Photo;
        }

        if (message.Location is not null)
        {
            return MessageType.Location;
        }

        if (message.Video is not null)
        {
            return MessageType.Unknown;
        }

        return MessageType.Unknown;
    }

    private static string DescribeNonText(Message message)
    {
        if (message.Photo?.Length > 0)
        {
            return "[photo]";
        }

        if (message.Location is not null)
        {
            return $"[location] lat={message.Location.Latitude};lng={message.Location.Longitude}";
        }

        if (message.Video is not null)
        {
            return "[video]";
        }

        return "[unknown]";
    }

    private static bool IsProviderMode(Domain.Entities.AppUser user)
    {
        if (user.Role == UserRole.Provider)
        {
            return true;
        }

        if (user.Role == UserRole.Both)
        {
            return user.Session?.State.StartsWith("P_", StringComparison.OrdinalIgnoreCase) == true;
        }

        return false;
    }

    private async Task<bool> TryHandleHumanHandoffMessageAsync(
        BotDbContext db,
        ITelegramBotClient botClient,
        string tenantId,
        Domain.Entities.AppUser user,
        Message message,
        CancellationToken cancellationToken)
    {
        var safeText = message.Text?.Trim();
        if (IsHumanHandoffRequestText(safeText))
        {
            var request = await _humanHandoff.RequestAsync(db, tenantId, user, cancellationToken);
            await _sender.SendTextAsync(
                db,
                botClient,
                tenantId,
                user.TelegramUserId,
                message.Chat.Id,
                request.ResponseText,
                null,
                user.Session?.ActiveJobId,
                cancellationToken);
            return true;
        }

        var openSession = await _humanHandoff.GetOpenSessionAsync(db, tenantId, user.TelegramUserId, cancellationToken);
        if (openSession is null)
        {
            return false;
        }

        await _humanHandoff.MarkActivityAsync(db, openSession, cancellationToken);
        // Atendimento humano aberto: a mensagem do cliente segue para o painel humano,
        // sem reenviar resposta automatica a cada interacao.
        return true;
    }

    private async Task<bool> TryHandleHumanHandoffCallbackAsync(
        BotDbContext db,
        ITelegramBotClient botClient,
        string tenantId,
        Domain.Entities.AppUser user,
        CallbackQuery callback,
        CallbackRoute route,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        if (route.Scope == "S" && route.Action == "ATD" && route.Arg1 == "REQ")
        {
            var request = await _humanHandoff.RequestAsync(db, tenantId, user, cancellationToken);
            await _sender.SendTextAsync(
                db,
                botClient,
                tenantId,
                user.TelegramUserId,
                chatId,
                request.ResponseText,
                null,
                user.Session?.ActiveJobId,
                cancellationToken);

            await botClient.AnswerCallbackQuery(
                callback.Id,
                request.IsAlreadyOpen ? "Atendimento humano ja esta ativo." : "Atendente acionado.",
                false,
                cancellationToken);

            return true;
        }

        var openSession = await _humanHandoff.GetOpenSessionAsync(db, tenantId, user.TelegramUserId, cancellationToken);
        if (openSession is null)
        {
            return false;
        }

        await _humanHandoff.MarkActivityAsync(db, openSession, cancellationToken);
        await botClient.AnswerCallbackQuery(
            callback.Id,
            "Atendimento humano ativo. Aguarde o atendente.",
            false,
            cancellationToken);
        return true;
    }

    private static bool IsHumanHandoffRequestText(string? text)
    {
        var safe = NormalizeText(text);
        if (safe.Length == 0)
        {
            return false;
        }

        return safe == "/atendente"
               || safe == "atendente"
               || safe == "humano"
               || safe == NormalizeText(MenuTexts.HumanHandoff)
               || safe.Contains("falar com atendente", StringComparison.Ordinal)
               || safe.Contains("falar com humano", StringComparison.Ordinal)
               || safe.Contains("atendimento humano", StringComparison.Ordinal);
    }

    private static string NormalizeText(string? text)
    {
        var normalized = (text ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder
            .ToString()
            .Normalize(NormalizationForm.FormC);
    }

    private static BotExecutionContext BuildContext(
        string tenantId,
        BotDbContext db,
        ITelegramBotClient botClient,
        Domain.Entities.AppUser user,
        TelegramRuntimeSettings runtime)
    {
        return new BotExecutionContext
        {
            TenantId = tenantId,
            Db = db,
            Bot = botClient,
            User = user,
            Session = user.Session!,
            Draft = UserContextService.ParseDraft(user.Session),
            Runtime = runtime
        };
    }
}
