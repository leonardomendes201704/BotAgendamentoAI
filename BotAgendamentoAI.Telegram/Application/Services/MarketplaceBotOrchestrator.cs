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
    private readonly ILogger<MarketplaceBotOrchestrator> _logger;

    public MarketplaceBotOrchestrator(
        IDbContextFactory<BotDbContext> dbFactory,
        UserContextService userContextService,
        ConversationHistoryService historyService,
        TelegramMessageSender sender,
        ClientFlowHandler clientFlow,
        ProviderFlowHandler providerFlow,
        ChatMediatorService chatMediator,
        ILogger<MarketplaceBotOrchestrator> logger)
    {
        _dbFactory = dbFactory;
        _userContextService = userContextService;
        _historyService = historyService;
        _sender = sender;
        _clientFlow = clientFlow;
        _providerFlow = providerFlow;
        _chatMediator = chatMediator;
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
                    ? KeyboardFactory.ProviderMenu()
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

        if (string.Equals(message.Text?.Trim(), "/start", StringComparison.OrdinalIgnoreCase))
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

        if (route.Scope == "U" && route.Action == "ROLE")
        {
            await HandleRoleSelectionAsync(db, botClient, tenantId, user, route.Arg1, chatId, cancellationToken);
            await botClient.AnswerCallbackQuery(callback.Id, "Perfil atualizado", false, cancellationToken);
            return;
        }

        var context = BuildContext(tenantId, db, botClient, user, runtime);

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
                    ? KeyboardFactory.ProviderMenu()
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
                BotMessages.ProviderHomeMenu(),
                KeyboardFactory.ProviderMenu(),
                null,
                cancellationToken);
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
