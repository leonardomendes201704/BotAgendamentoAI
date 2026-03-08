using System.Net.Http;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotAgendamentoAI.Telegram.Application.Callback;
using BotAgendamentoAI.Telegram.Application.Common;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using BotAgendamentoAI.Telegram.TelegramCompat;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram.Features.Client;

public sealed class ClientFlowHandler
{
    private const string DefaultCloseConfirmationText = "O atendimento sera encerrado e qualquer pedido em andamento sera descartado. Deseja continuar?";
    private const string DefaultClosingText = "Atendimento encerrado. Se precisar de alguma coisa, e so mandar um Oi.";
    private static readonly HttpClient ViaCepHttpClient = BuildViaCepHttpClient();
    private static readonly HttpClient GeocodeHttpClient = BuildGeocodeHttpClient();
    private static readonly SemaphoreSlim NominatimThrottle = new(1, 1);
    private static DateTimeOffset LastNominatimRequestUtc = DateTimeOffset.MinValue;
    private static readonly Regex ClientRegistrationCpfRegex = new(@"\D", RegexOptions.Compiled);
    private static readonly Regex ClientRegistrationNumberRegex = new(@"^\s*(?<number>\d+[A-Za-z]?)\s*(?:[,\/-]?\s*(?<complement>.*))?$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TelegramMessageSender _sender;
    private readonly JobWorkflowService _jobWorkflow;
    private readonly IPhotoValidator _photoValidator;
    private readonly BotExceptionLogService _exceptionLog;
    private readonly ILogger<ClientFlowHandler> _logger;
    private readonly CalendarSyncQueueService? _calendarQueue;
    private readonly AvailabilityService? _availability;

    public ClientFlowHandler(
        TelegramMessageSender sender,
        JobWorkflowService jobWorkflow,
        IPhotoValidator photoValidator,
        BotExceptionLogService exceptionLog,
        ILogger<ClientFlowHandler> logger,
        CalendarSyncQueueService? calendarQueue = null,
        AvailabilityService? availability = null)
    {
        _sender = sender;
        _jobWorkflow = jobWorkflow;
        _photoValidator = photoValidator;
        _exceptionLog = exceptionLog;
        _logger = logger;
        _calendarQueue = calendarQueue;
        _availability = availability;
    }

    public bool IsClientRegistrationPending(AppUser user)
    {
        if (user.Role == UserRole.Provider)
        {
            return false;
        }

        return !IsClientRegistrationComplete(user.ClientProfile);
    }

    public async Task StartOrResumeRegistrationAsync(
        BotExecutionContext context,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var profile = await GetOrCreateClientProfileAsync(context, cancellationToken);
        var nextState = ResolveClientRegistrationState(context, profile);
        if (nextState is null)
        {
            await CompleteClientRegistrationAsync(context, profile, chatId, cancellationToken);
            return;
        }

        await context.Db.SaveChangesAsync(cancellationToken);
        await SendClientRegistrationPromptAsync(context, chatId, profile, null, cancellationToken);
    }

    public async Task<bool> TryHandleRegistrationTextAsync(
        BotExecutionContext context,
        Message message,
        CancellationToken cancellationToken)
    {
        if (!IsClientRegistrationPending(context.User))
        {
            return false;
        }

        var profile = await GetOrCreateClientProfileAsync(context, cancellationToken);
        var nextState = ResolveClientRegistrationState(context, profile);
        if (nextState is null)
        {
            await CompleteClientRegistrationAsync(context, profile, message.Chat.Id, cancellationToken);
            return true;
        }

        var safeText = (message.Text ?? string.Empty).Trim();
        if (string.Equals(safeText, MenuTexts.EndAttendance, StringComparison.OrdinalIgnoreCase))
        {
            await PromptEndAttendanceConfirmationAsync(context, message.Chat.Id, cancellationToken);
            return true;
        }

        if (string.Equals(safeText, MenuTexts.Cancel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(safeText, MenuTexts.Back, StringComparison.OrdinalIgnoreCase))
        {
            await SendClientRegistrationPromptAsync(
                context,
                message.Chat.Id,
                profile,
                "Conclua seu cadastro para continuar.",
                cancellationToken);
            return true;
        }

        switch (nextState)
        {
            case BotStates.C_REG_NAME:
                await HandleClientRegistrationNameAsync(context, profile, message.Chat.Id, safeText, cancellationToken);
                return true;

            case BotStates.C_REG_EMAIL:
                await HandleClientRegistrationEmailAsync(context, profile, message.Chat.Id, safeText, cancellationToken);
                return true;

            case BotStates.C_REG_CPF:
                await HandleClientRegistrationCpfAsync(context, profile, message.Chat.Id, safeText, cancellationToken);
                return true;

            case BotStates.C_REG_CEP:
                await HandleClientRegistrationCepAsync(context, profile, message.Chat.Id, safeText, cancellationToken);
                return true;

            case BotStates.C_REG_ADDRESS_NUMBER:
                await HandleClientRegistrationAddressNumberAsync(context, profile, message.Chat.Id, safeText, cancellationToken);
                return true;

            case BotStates.C_REG_ADDRESS_CONFIRM:
                await SendClientRegistrationPromptAsync(
                    context,
                    message.Chat.Id,
                    profile,
                    "Use os botoes para confirmar o endereco.",
                    cancellationToken);
                return true;

            case BotStates.C_REG_PHONE:
                await HandleClientRegistrationPhoneAsync(context, profile, message, safeText, cancellationToken);
                return true;

            default:
                await StartOrResumeRegistrationAsync(context, message.Chat.Id, cancellationToken);
                return true;
        }
    }

    public async Task<bool> TryHandleRegistrationCallbackAsync(
        BotExecutionContext context,
        CallbackRoute route,
        CallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (!IsClientRegistrationPending(context.User))
        {
            return false;
        }

        var chatId = callback.Message?.Chat.Id ?? context.User.TelegramUserId;
        var profile = await GetOrCreateClientProfileAsync(context, cancellationToken);
        var nextState = ResolveClientRegistrationState(context, profile);
        if (nextState is null)
        {
            await CompleteClientRegistrationAsync(context, profile, chatId, cancellationToken);
            return true;
        }

        if (route.Scope == "C" && route.Action == "ADDR" && string.Equals(nextState, BotStates.C_REG_ADDRESS_CONFIRM, StringComparison.Ordinal))
        {
            await HandleClientRegistrationAddressConfirmationAsync(context, profile, chatId, route.Arg1, cancellationToken);
            return true;
        }

        if (route.Scope == "NAV" && route.Action == "CLOSE")
        {
            await PromptEndAttendanceConfirmationAsync(context, chatId, cancellationToken);
            return true;
        }

        if (route.Scope == "S" && route.Action == "END")
        {
            switch ((route.Arg1 ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "ASK":
                    await PromptEndAttendanceConfirmationAsync(context, chatId, cancellationToken);
                    return true;

                case "OK":
                    await CompleteEndAttendanceAsync(context, chatId, cancellationToken);
                    return true;

                case "KEEP":
                    await SendClientRegistrationPromptAsync(context, chatId, profile, null, cancellationToken);
                    return true;
            }
        }

        await SendClientRegistrationPromptAsync(
            context,
            chatId,
            profile,
            "Conclua seu cadastro antes de usar o menu do cliente.",
            cancellationToken);
        return true;
    }

    public async Task HandleTextAsync(BotExecutionContext context, Message message, CancellationToken cancellationToken)
    {
        var text = (message.Text ?? string.Empty).Trim();
        var state = context.Session.State;
        var normalizedText = NormalizeClientMenuInput(text, state);

        if (string.Equals(normalizedText, MenuTexts.EndAttendance, StringComparison.OrdinalIgnoreCase))
        {
            await PromptEndAttendanceConfirmationAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(text, MenuTexts.Cancel, StringComparison.OrdinalIgnoreCase))
        {
            await GoHomeAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(text, MenuTexts.Back, StringComparison.OrdinalIgnoreCase))
        {
            await GoHomeAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(normalizedText, MenuTexts.ClientSwitchToProvider, StringComparison.OrdinalIgnoreCase)
            && context.User.Role == UserRole.Both)
        {
            await SwitchToProviderAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(normalizedText, MenuTexts.ClientRequestService, StringComparison.OrdinalIgnoreCase))
        {
            await StartWizardAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(normalizedText, MenuTexts.ClientMyBookings, StringComparison.OrdinalIgnoreCase))
        {
            await SendMyJobsAsync(context, message.Chat.Id, 0, cancellationToken);
            return;
        }

        if (string.Equals(normalizedText, MenuTexts.ClientHelp, StringComparison.OrdinalIgnoreCase))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Use o menu para pedir servico, acompanhar agendamentos e conversar com o prestador.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        if (string.Equals(normalizedText, MenuTexts.ClientFavorites, StringComparison.OrdinalIgnoreCase))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Favoritos ainda nao foi configurado para sua conta.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        switch (state)
        {
            case BotStates.C_DESCRIBE_PROBLEM:
                await HandleDescriptionAsync(context, message.Chat.Id, text, cancellationToken);
                return;

            case BotStates.C_COLLECT_PHOTOS:
                await HandlePhotoCollectionTextAsync(context, message, text, cancellationToken);
                return;

            case BotStates.C_PICK_CATEGORY:
                await SendCategorySelectionAsync(
                    context,
                    message.Chat.Id,
                    "Escolha a categoria usando os botoes abaixo.",
                    cancellationToken);
                return;

            case BotStates.C_LOCATION:
                await HandleLocationAsync(context, message, text, cancellationToken);
                return;

            case BotStates.C_CONTACT_NAME:
                await HandleContactNameAsync(context, message.Chat.Id, text, cancellationToken);
                return;

            case BotStates.C_CONTACT_PHONE:
                await HandleContactPhoneAsync(context, message, text, cancellationToken);
                return;

            case BotStates.C_RATING:
                var hasPendingRating = await PromptPendingRatingAsync(context, message.Chat.Id, null, cancellationToken);
                if (!hasPendingRating)
                {
                    context.Session.State = BotStates.C_HOME;
                    context.Session.UpdatedAt = DateTimeOffset.UtcNow;
                    await context.Db.SaveChangesAsync(cancellationToken);

                    await SendClientHomeMenuAsync(
                        context,
                        message.Chat.Id,
                        context.Session.ActiveJobId,
                        cancellationToken);
                }
                return;

            default:
                await SendClientHomeMenuAsync(
                    context,
                    message.Chat.Id,
                    context.Session.ActiveJobId,
                    cancellationToken);
                return;
        }
    }

    public async Task HandlePhotoAsync(BotExecutionContext context, Message message, CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Session.State, BotStates.C_COLLECT_PHOTOS, StringComparison.Ordinal))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Foto recebida. Para enviar fotos do problema, escolha 'Pedir servico' no menu.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        if (message.Photo is null || message.Photo.Length == 0)
        {
            return;
        }

        var fileId = message.Photo[^1].FileId;
        if (!context.Draft.PhotoFileIds.Contains(fileId, StringComparer.Ordinal))
        {
            context.Draft.PhotoFileIds.Add(fileId);
        }

        UserContextService.SaveDraft(context.Session, context.Draft);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            message.Chat.Id,
            $"Foto adicionada ({context.Draft.PhotoFileIds.Count}).",
            KeyboardFactory.PhotoCollectMenu(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    public async Task HandleLocationMessageAsync(BotExecutionContext context, Message message, CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Session.State, BotStates.C_LOCATION, StringComparison.Ordinal))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Localizacao recebida fora do fluxo. Use o menu para iniciar um pedido.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            message.Chat.Id,
            "Para validar corretamente o endereco, envie somente o CEP por texto.",
            KeyboardFactory.CepRequestKeyboard(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    public async Task<bool> HandleCallbackAsync(BotExecutionContext context, CallbackRoute route, CallbackQuery callback, CancellationToken cancellationToken)
    {
        var chatId = callback.Message?.Chat.Id ?? context.User.TelegramUserId;

        if (route.Scope == "NAV" && route.Action == "CANCEL")
        {
            await GoHomeAsync(context, chatId, cancellationToken);
            return true;
        }

        if (route.Scope == "NAV" && route.Action == "BACK")
        {
            await GoHomeAsync(context, chatId, cancellationToken);
            return true;
        }

        if (route.Scope == "NAV" && route.Action == "CLOSE")
        {
            await PromptEndAttendanceConfirmationAsync(context, chatId, cancellationToken);
            return true;
        }

        if (route.Scope == "S" && route.Action == "END")
        {
            switch ((route.Arg1 ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "ASK":
                    await PromptEndAttendanceConfirmationAsync(context, chatId, cancellationToken);
                    return true;

                case "OK":
                    await CompleteEndAttendanceAsync(context, chatId, cancellationToken);
                    return true;

                case "KEEP":
                    return true;
            }
        }

        if (route.Scope == "C" && route.Action == "HOME")
        {
            await HandleHomeCallbackAsync(context, chatId, route.Arg1 ?? string.Empty, cancellationToken);
            return true;
        }

        if (route.Scope == "C" && route.Action == "MY")
        {
            if (!int.TryParse(route.Arg1, out var offset))
            {
                offset = 0;
            }

            await SendMyJobsAsync(context, chatId, Math.Max(0, offset), cancellationToken);
            return true;
        }

        if (route.Scope == "J" && long.TryParse(route.Action, out var chatJobId) && route.Arg1 == "CHAT" && route.Arg2 == "EXIT")
        {
            context.Session.IsChatActive = false;
            context.Session.ChatPeerUserId = null;
            context.Session.ChatJobId = null;
            context.Session.ActiveJobId = chatJobId;
            context.Session.State = BotStates.C_TRACKING;
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                BotMessages.ChatClosed(),
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                chatJobId,
                cancellationToken);
            return true;
        }

        if (route.Scope == "J" && long.TryParse(route.Action, out var jobId))
        {
            if (route.Arg1 == "CAN")
            {
                await HandleClientJobCancelAsync(context, chatId, jobId, cancellationToken);
                return true;
            }

            if (route.Arg1 == "RS")
            {
                await HandleClientJobRescheduleAsync(context, chatId, jobId, route, cancellationToken);
                return true;
            }
        }

        if (route.Scope == "C" && route.Action == "CAT")
        {
            ServiceCategoryEntity? category = null;
            if (long.TryParse(route.Arg1, out var categoryId))
            {
                category = await context.Db.ServiceCategories
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        x => x.Id == categoryId && x.TenantId == context.TenantId,
                        cancellationToken);
            }
            else
            {
                var normalized = NormalizeCategoryKey(route.Arg1);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    category = await context.Db.ServiceCategories
                        .AsNoTracking()
                        .Where(x => x.TenantId == context.TenantId)
                        .FirstOrDefaultAsync(
                            x => x.NormalizedName == normalized || x.Name == route.Arg1,
                            cancellationToken);
                }
            }

            if (category is null)
            {
                await SendCategorySelectionAsync(
                    context,
                    chatId,
                    "Nao consegui identificar essa categoria. Selecione novamente.",
                    cancellationToken);
                return true;
            }

            context.Draft.Category = category.Name;
            UserContextService.SaveDraft(context.Session, context.Draft);
            UserContextService.SetState(context.Session, BotStates.C_DESCRIBE_PROBLEM);
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                BotMessages.AskDescription(),
                null,
                context.Session.ActiveJobId,
                cancellationToken);

            return true;
        }

        if (route.Scope == "C" && route.Action == "PH" && route.Arg1 == "DONE")
        {
            context.Draft.AddressText = null;
            context.Draft.Cep = null;
            context.Draft.AddressBaseFromCep = null;
            context.Draft.WaitingAddressNumber = false;
            context.Draft.WaitingAddressConfirmation = false;
            context.Draft.Latitude = null;
            context.Draft.Longitude = null;
            UserContextService.SetState(context.Session, BotStates.C_LOCATION);
            UserContextService.SaveDraft(context.Session, context.Draft);
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                BotMessages.AskLocation(),
                KeyboardFactory.CepRequestKeyboard(),
                context.Session.ActiveJobId,
                cancellationToken);

            return true;
        }

        if (route.Scope == "C" && route.Action == "ADDR")
        {
            if (route.Arg1 == "EDIT")
            {
                context.Draft.AddressText = null;
                context.Draft.Cep = null;
                context.Draft.AddressBaseFromCep = null;
                context.Draft.WaitingAddressNumber = false;
                context.Draft.WaitingAddressConfirmation = false;
                context.Draft.Latitude = null;
                context.Draft.Longitude = null;
                UserContextService.SaveDraft(context.Session, context.Draft);
                UserContextService.SetState(context.Session, BotStates.C_LOCATION);
                await context.Db.SaveChangesAsync(cancellationToken);

                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    BotMessages.AskLocation(),
                    KeyboardFactory.CepRequestKeyboard(),
                    context.Session.ActiveJobId,
                    cancellationToken);
                return true;
            }

            if (route.Arg1 == "OK")
            {
                if (string.IsNullOrWhiteSpace(context.Draft.AddressText))
                {
                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        "Endereco ainda nao foi preenchido. Envie o CEP para continuar.",
                        KeyboardFactory.CepRequestKeyboard(),
                        context.Session.ActiveJobId,
                        cancellationToken);
                    return true;
                }

                context.Draft.WaitingAddressNumber = false;
                context.Draft.WaitingAddressConfirmation = false;
                context.Draft.AddressBaseFromCep = null;
                await AdvanceToScheduleAsync(context, chatId, cancellationToken);
                return true;
            }

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Use os botoes para confirmar o endereco.",
                KeyboardFactory.AddressConfirmation(),
                context.Session.ActiveJobId,
                cancellationToken);
            return true;
        }

        if (route.Scope == "C" && route.Action == "SCH")
        {
            if (route.Arg1 == "URG")
            {
                if (_availability is not null)
                {
                    var request = await BuildAvailabilityRequestAsync(
                        context,
                        context.User.Id,
                        null,
                        null,
                        false,
                        cancellationToken);
                    var check = await _availability.CheckSlotAvailabilityAsync(
                        context.Db,
                        request,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                    if (!check.IsAvailable)
                    {
                        await _sender.SendTextAsync(
                            context.Db,
                            context.Bot,
                            context.TenantId,
                            context.User.TelegramUserId,
                            chatId,
                            "Voce ja possui um agendamento no periodo atual. Escolha outro horario em 'Agendar'.",
                            KeyboardFactory.ScheduleMode(),
                            context.Session.ActiveJobId,
                            cancellationToken);
                        return true;
                    }
                }

                context.Draft.IsUrgent = true;
                context.Draft.ScheduledAt = DateTimeOffset.UtcNow;
                UserContextService.SaveDraft(context.Session, context.Draft);
                UserContextService.SetState(context.Session, BotStates.C_PREFERENCES);
                await context.Db.SaveChangesAsync(cancellationToken);

                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    BotMessages.AskPreference(),
                    KeyboardFactory.Preferences(),
                    context.Session.ActiveJobId,
                    cancellationToken);

                return true;
            }

            if (route.Arg1 == "TOD")
            {
                if (_availability is not null)
                {
                    var request = await BuildAvailabilityRequestAsync(
                        context,
                        context.User.Id,
                        null,
                        null,
                        true,
                        cancellationToken);
                    var nowLocal = request.NowLocal;
                    var dayToken = nowLocal.ToString("yyyyMMdd");
                    var slots = await _availability.GetAvailableTimeSlotsAsync(
                        context.Db,
                        request,
                        dayToken,
                        cancellationToken);
                    if (slots.Count == 0)
                    {
                        await _sender.SendTextAsync(
                            context.Db,
                            context.Bot,
                            context.TenantId,
                            context.User.TelegramUserId,
                            chatId,
                            "Nao encontrei horarios livres para hoje. Escolha 'Agendar' para ver os proximos dias.",
                            KeyboardFactory.ScheduleMode(),
                            context.Session.ActiveJobId,
                            cancellationToken);
                        return true;
                    }

                    var firstSlot = slots[0];
                    if (!TryParseSchedule(dayToken, firstSlot, context.Runtime.TimeZone, out var scheduleToday))
                    {
                        await _sender.SendTextAsync(
                            context.Db,
                            context.Bot,
                            context.TenantId,
                            context.User.TelegramUserId,
                            chatId,
                            "Nao consegui montar um horario disponivel para hoje. Tente Agendar.",
                            KeyboardFactory.ScheduleMode(),
                            context.Session.ActiveJobId,
                            cancellationToken);
                        return true;
                    }

                    context.Draft.IsUrgent = false;
                    context.Draft.ScheduledAt = scheduleToday;
                }
                else
                {
                    var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, context.Runtime.TimeZone);
                    context.Draft.IsUrgent = false;
                    context.Draft.ScheduledAt = nowLocal.Date.AddHours(Math.Max(nowLocal.Hour + 1, 9));
                }

                UserContextService.SaveDraft(context.Session, context.Draft);
                UserContextService.SetState(context.Session, BotStates.C_PREFERENCES);
                await context.Db.SaveChangesAsync(cancellationToken);

                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    BotMessages.AskPreference(),
                    KeyboardFactory.Preferences(),
                    context.Session.ActiveJobId,
                    cancellationToken);

                return true;
            }

            if (route.Arg1 == "CAL")
            {
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, context.Runtime.TimeZone);
                InlineKeyboardMarkup dayKeyboard;
                if (_availability is not null)
                {
                    var request = await BuildAvailabilityRequestAsync(
                        context,
                        context.User.Id,
                        null,
                        null,
                        true,
                        cancellationToken);
                    var days = await _availability.GetAvailableDaysAsync(context.Db, request, cancellationToken);
                    if (days.Count == 0)
                    {
                        await _sender.SendTextAsync(
                            context.Db,
                            context.Bot,
                            context.TenantId,
                            context.User.TelegramUserId,
                            chatId,
                            "Nao ha horarios disponiveis para os proximos dias. Tente novamente mais tarde.",
                            KeyboardFactory.ScheduleMode(),
                            context.Session.ActiveJobId,
                            cancellationToken);
                        return true;
                    }

                    dayKeyboard = KeyboardFactory.DaySelection(days);
                }
                else
                {
                    dayKeyboard = KeyboardFactory.DaySelection(nowLocal);
                }

                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Selecione o dia:",
                    dayKeyboard,
                    context.Session.ActiveJobId,
                    cancellationToken);

                return true;
            }
        }

        if (route.Scope == "C" && route.Action == "DAY")
        {
            if (_availability is not null)
            {
                if (!AvailabilityService.TryParseDayToken(route.Arg1, out _))
                {
                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        "Dia invalido. Selecione novamente.",
                        KeyboardFactory.ScheduleMode(),
                        context.Session.ActiveJobId,
                        cancellationToken);
                    return true;
                }

                var request = await BuildAvailabilityRequestAsync(
                    context,
                    context.User.Id,
                    null,
                    null,
                    true,
                    cancellationToken);
                var slots = await _availability.GetAvailableTimeSlotsAsync(
                    context.Db,
                    request,
                    route.Arg1,
                    cancellationToken);
                if (slots.Count == 0)
                {
                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        "Esse dia ficou sem horarios livres. Escolha outro dia.",
                        KeyboardFactory.ScheduleMode(),
                        context.Session.ActiveJobId,
                        cancellationToken);
                    return true;
                }

                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Agora selecione o horario:",
                    KeyboardFactory.TimeSelection(route.Arg1, slots),
                    context.Session.ActiveJobId,
                    cancellationToken);
                return true;
            }

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Agora selecione o horario:",
                KeyboardFactory.TimeSelection(route.Arg1),
                context.Session.ActiveJobId,
                cancellationToken);

            return true;
        }

        if (route.Scope == "C" && route.Action == "TIM")
        {
            if (!TryParseSchedule(route.Arg1, route.Arg2, context.Runtime.TimeZone, out var scheduledAt))
            {
                return true;
            }

            if (_availability is not null)
            {
                var request = await BuildAvailabilityRequestAsync(
                    context,
                    context.User.Id,
                    null,
                    null,
                    true,
                    cancellationToken);
                var check = await _availability.CheckSlotAvailabilityAsync(
                    context.Db,
                    request,
                    scheduledAt,
                    cancellationToken);
                if (!check.IsAvailable)
                {
                    var slots = await _availability.GetAvailableTimeSlotsAsync(
                        context.Db,
                        request,
                        route.Arg1,
                        cancellationToken);
                    var keyboard = slots.Count > 0
                        ? KeyboardFactory.TimeSelection(route.Arg1, slots)
                        : KeyboardFactory.ScheduleMode();
                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        "Esse horario ficou indisponivel. Escolha outro horario.",
                        keyboard,
                        context.Session.ActiveJobId,
                        cancellationToken);
                    return true;
                }
            }

            context.Draft.IsUrgent = false;
            context.Draft.ScheduledAt = scheduledAt;
            UserContextService.SaveDraft(context.Session, context.Draft);
            UserContextService.SetState(context.Session, BotStates.C_PREFERENCES);
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                BotMessages.AskPreference(),
                KeyboardFactory.Preferences(),
                context.Session.ActiveJobId,
                cancellationToken);

            return true;
        }

        if (route.Scope == "C" && route.Action == "PRF")
        {
            context.Draft.PreferenceCode = route.Arg1;
            context.Draft.ContactName = string.IsNullOrWhiteSpace(context.Draft.ContactName)
                ? (context.User.Name ?? string.Empty).Trim()
                : context.Draft.ContactName?.Trim();
            context.Draft.ContactPhone = string.IsNullOrWhiteSpace(context.Draft.ContactPhone)
                ? NormalizePhoneIfPossible(context.User.Phone)
                : context.Draft.ContactPhone?.Trim();
            UserContextService.SaveDraft(context.Session, context.Draft);
            UserContextService.SetState(context.Session, BotStates.C_CONTACT_NAME);
            await context.Db.SaveChangesAsync(cancellationToken);

            var suggestedName = string.IsNullOrWhiteSpace(context.Draft.ContactName)
                ? string.Empty
                : $" (sugestao: {context.Draft.ContactName})";
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                $"{BotMessages.AskContactName()}{suggestedName}",
                null,
                context.Session.ActiveJobId,
                cancellationToken);

            return true;
        }

        if (route.Scope == "C" && route.Action == "CONF")
        {
            if (route.Arg1 == "EDIT")
            {
                await StartWizardAsync(context, chatId, cancellationToken);
                return true;
            }

            if (route.Arg1 == "CANCEL")
            {
                await GoHomeAsync(context, chatId, cancellationToken);
                return true;
            }

            if (route.Arg1 == "OK")
            {
                if (_availability is not null && context.Draft.ScheduledAt.HasValue)
                {
                    var request = await BuildAvailabilityRequestAsync(
                        context,
                        context.User.Id,
                        null,
                        null,
                        false,
                        cancellationToken);
                    var check = await _availability.CheckSlotAvailabilityAsync(
                        context.Db,
                        request,
                        context.Draft.ScheduledAt.Value,
                        cancellationToken);
                    if (!check.IsAvailable)
                    {
                        UserContextService.SetState(context.Session, BotStates.C_SCHEDULE);
                        await context.Db.SaveChangesAsync(cancellationToken);

                        await _sender.SendTextAsync(
                            context.Db,
                            context.Bot,
                            context.TenantId,
                            context.User.TelegramUserId,
                            chatId,
                            "Esse horario acabou de ficar indisponivel. Escolha outro horario.",
                            KeyboardFactory.ScheduleMode(),
                            context.Session.ActiveJobId,
                            cancellationToken);
                        return true;
                    }
                }

                if (context.Runtime.EnablePhotoValidation)
                {
                    var validation = await _photoValidator.ValidateAsync(
                        context.Draft.Category ?? string.Empty,
                        context.Draft.PhotoFileIds,
                        cancellationToken);

                    if (!validation.Ok)
                    {
                        await _sender.SendTextAsync(
                            context.Db,
                            context.Bot,
                            context.TenantId,
                            context.User.TelegramUserId,
                            chatId,
                            validation.Message,
                            KeyboardFactory.PhotoCollectMenu(),
                            context.Session.ActiveJobId,
                            cancellationToken);

                        UserContextService.SetState(context.Session, BotStates.C_COLLECT_PHOTOS);
                        await context.Db.SaveChangesAsync(cancellationToken);
                        return true;
                    }
                }

                Job job;
                try
                {
                    job = await _jobWorkflow.ConfirmDraftAsync(context, chatId, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        ex.Message,
                        KeyboardFactory.ScheduleMode(),
                        context.Session.ActiveJobId,
                        cancellationToken);
                    UserContextService.SetState(context.Session, BotStates.C_SCHEDULE);
                    await context.Db.SaveChangesAsync(cancellationToken);
                    return true;
                }

                context.Session.ActiveJobId = job.Id;
                context.Session.State = BotStates.C_TRACKING;
                context.Session.DraftJson = "{}";
                context.Session.UpdatedAt = DateTimeOffset.UtcNow;
                await context.Db.SaveChangesAsync(cancellationToken);

                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Acompanhe seu pedido no menu 'Meus agendamentos'.",
                    KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                    job.Id,
                    cancellationToken);

                return true;
            }
        }

        if (route.Scope == "R")
        {
            if (!long.TryParse(route.Action, out var ratingJobId) || !int.TryParse(route.Arg1, out var stars))
            {
                return true;
            }

            stars = Math.Clamp(stars, 1, 5);
            var job = await context.Db.Jobs.FirstOrDefaultAsync(x => x.Id == ratingJobId && x.ClientUserId == context.User.Id, cancellationToken);
            if (job is null || !job.ProviderUserId.HasValue || job.Status != JobStatus.Finished)
            {
                return true;
            }

            var already = await context.Db.Ratings.AnyAsync(x => x.JobId == ratingJobId, cancellationToken);
            if (!already)
            {
                context.Db.Ratings.Add(new Rating
                {
                    JobId = ratingJobId,
                    ClientUserId = context.User.Id,
                    ProviderUserId = job.ProviderUserId.Value,
                    Stars = stars,
                    CreatedAt = DateTimeOffset.UtcNow
                });

                await context.Db.SaveChangesAsync(cancellationToken);
            }

            var hasMorePendingRatings = await PromptPendingRatingAsync(context, chatId, null, cancellationToken);
            if (hasMorePendingRatings)
            {
                return true;
            }

            context.Session.State = BotStates.C_HOME;
            context.Session.ActiveJobId = ratingJobId;
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Obrigado pela avaliacao!",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                ratingJobId,
                cancellationToken);

            return true;
        }

        return false;
    }

    public async Task OpenChatAsync(BotExecutionContext context, long jobId, ChatId chatId, CancellationToken cancellationToken)
    {
        var job = await context.Db.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == jobId && x.ClientUserId == context.User.Id, cancellationToken);

        if (job is null || !CanOpenChat(job.Status, job.ProviderUserId.HasValue))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Chat indisponivel para esse pedido.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                jobId,
                cancellationToken);
            return;
        }

        context.Session.ActiveJobId = job.Id;
        context.Session.ChatJobId = job.Id;
        context.Session.ChatPeerUserId = job.ProviderUserId;
        context.Session.IsChatActive = true;
        context.Session.State = BotStates.CHAT_MEDIATED;
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            BotMessages.ChatOpened(),
            KeyboardFactory.ClientChatActions(job.Id),
            job.Id,
            cancellationToken);
    }

    public async Task SendMyJobsAsync(BotExecutionContext context, ChatId chatId, int offset, CancellationToken cancellationToken)
    {
        var safeOffset = Math.Max(0, offset);
        const int pageSize = 5;

        var jobsQuery = context.Db.Jobs
            .AsNoTracking()
            .Where(x => x.ClientUserId == context.User.Id)
            .Where(x => x.Status != JobStatus.Cancelled);

        var total = await jobsQuery
            .CountAsync(cancellationToken);

        var jobs = await jobsQuery
            .OrderByDescending(x => x.Id)
            .Skip(safeOffset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Voce ainda nao possui agendamentos.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                null,
                cancellationToken);

            return;
        }

        foreach (var job in jobs)
        {
            var text = BuildClientJobCard(job, context.Runtime.TimeZone);
            var keyboard = BuildClientJobActions(job);

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                text,
                keyboard,
                job.Id,
                cancellationToken);
        }

        var navButtons = new List<InlineKeyboardButton>();
        if (safeOffset > 0)
        {
            var previous = Math.Max(0, safeOffset - pageSize);
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Anterior", $"C:MY:{previous}"));
        }

        if (safeOffset + jobs.Count < total)
        {
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Proximos", $"C:MY:{safeOffset + jobs.Count}"));
        }

        if (navButtons.Count > 0)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Navegacao dos agendamentos:",
                new InlineKeyboardMarkup(new[] { navButtons.ToArray() }),
                null,
                cancellationToken);
        }
    }

    private async Task<bool> PromptPendingRatingAsync(
        BotExecutionContext context,
        ChatId chatId,
        Job? knownPendingJob,
        CancellationToken cancellationToken)
    {
        var pendingJob = knownPendingJob ?? await GetOldestPendingRatingJobAsync(context, cancellationToken);
        if (pendingJob is null)
        {
            return false;
        }

        context.Session.ActiveJobId = pendingJob.Id;
        context.Session.State = BotStates.C_RATING;
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);

        var card = BuildClientJobCard(pendingJob, context.Runtime.TimeZone);
        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            "Voce possui agendamento finalizado sem avaliacao. Antes de criar novo pedido, avalie este atendimento:\n\n" + card,
            KeyboardFactory.Rating(pendingJob.Id),
            pendingJob.Id,
            cancellationToken);
        return true;
    }

    private async Task<Job?> GetOldestPendingRatingJobAsync(BotExecutionContext context, CancellationToken cancellationToken)
    {
        return await context.Db.Jobs
            .AsNoTracking()
            .Where(x => x.TenantId == context.TenantId
                        && x.ClientUserId == context.User.Id
                        && x.Status == JobStatus.Finished)
            .Where(x => !context.Db.Ratings.Any(r => r.JobId == x.Id))
            .OrderBy(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string BuildClientJobCard(Job job, TimeZoneInfo timeZone)
    {
        var when = job.IsUrgent
            ? "Urgente"
            : (job.ScheduledAt.HasValue
                ? TimeZoneInfo.ConvertTime(job.ScheduledAt.Value, timeZone).ToString("dd/MM/yyyy HH:mm")
                : "Nao definido");

        var status = FormatJobStatus(job.Status);

        var providerStatus = job.ProviderUserId.HasValue ? "Atribuido" : "Aguardando atribuicao";
        var address = string.IsNullOrWhiteSpace(job.AddressText) ? "Nao informado" : job.AddressText.Trim();
        var description = FormatCardField(job.Description, 180, "Nao informada");

        return
            $"📋 Agendamento #{job.Id}\n" +
            $"Categoria: {job.Category}\n" +
            $"Descricao: {description}\n" +
            $"Status: {status}\n" +
            $"Quando: {when}\n" +
            $"Prestador: {providerStatus}\n" +
            $"Endereco: {address}";
    }

    private static string FormatCardField(string? value, int maxLength, string fallback)
    {
        var safe = (value ?? string.Empty).Trim();
        if (safe.Length == 0)
        {
            return fallback;
        }

        if (safe.Length <= maxLength)
        {
            return safe;
        }

        return safe[..maxLength].TrimEnd() + "...";
    }

    public async Task StartWizardAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        if (await PromptPendingRatingAsync(context, chatId, null, cancellationToken))
        {
            return;
        }

        context.Session.DraftJson = "{}";
        context.Session.ActiveJobId = null;
        context.Session.ChatJobId = null;
        context.Session.ChatPeerUserId = null;
        context.Session.IsChatActive = false;
        context.Session.State = BotStates.C_PICK_CATEGORY;
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;

        context.Draft.Category = null;
        context.Draft.Description = null;
        context.Draft.AddressText = null;
        context.Draft.Cep = null;
        context.Draft.AddressBaseFromCep = null;
        context.Draft.WaitingAddressNumber = false;
        context.Draft.WaitingAddressConfirmation = false;
        context.Draft.Latitude = null;
        context.Draft.Longitude = null;
        context.Draft.IsUrgent = false;
        context.Draft.ScheduledAt = null;
        context.Draft.PreferenceCode = null;
        context.Draft.ContactName = null;
        context.Draft.ContactPhone = null;
        context.Draft.PhotoFileIds.Clear();

        UserContextService.SaveDraft(context.Session, context.Draft);
        await context.Db.SaveChangesAsync(cancellationToken);

        await SendCategorySelectionAsync(
            context,
            chatId,
            null,
            cancellationToken);
    }

    private async Task HandleDescriptionAsync(BotExecutionContext context, ChatId chatId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 5)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Descreva com mais detalhes (minimo 5 caracteres).",
                null,
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        context.Draft.Description = text;
        UserContextService.SaveDraft(context.Session, context.Draft);
        UserContextService.SetState(context.Session, BotStates.C_COLLECT_PHOTOS);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            BotMessages.AskPhotos(),
            KeyboardFactory.PhotoCollectMenu(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task HandlePhotoCollectionTextAsync(BotExecutionContext context, Message message, string text, CancellationToken cancellationToken)
    {
        if (string.Equals(text, MenuTexts.FinishPhotos, StringComparison.OrdinalIgnoreCase))
        {
            context.Draft.AddressText = null;
            context.Draft.Cep = null;
            context.Draft.AddressBaseFromCep = null;
            context.Draft.WaitingAddressNumber = false;
            context.Draft.WaitingAddressConfirmation = false;
            context.Draft.Latitude = null;
            context.Draft.Longitude = null;
            UserContextService.SetState(context.Session, BotStates.C_LOCATION);
            UserContextService.SaveDraft(context.Session, context.Draft);
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                BotMessages.AskLocation(),
                KeyboardFactory.CepRequestKeyboard(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            message.Chat.Id,
            "Envie fotos ou toque em 'Concluir fotos'.",
            KeyboardFactory.PhotoCollectMenu(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task HandleLocationAsync(BotExecutionContext context, Message message, string text, CancellationToken cancellationToken)
    {
        var safeText = (text ?? string.Empty).Trim();

        if (context.Draft.WaitingAddressConfirmation)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Use os botoes para confirmar o endereco: Correto ou Alterar.",
                KeyboardFactory.AddressConfirmation(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        if (context.Draft.WaitingAddressNumber)
        {
            var numberAndComplement = safeText;
            if (string.IsNullOrWhiteSpace(numberAndComplement))
            {
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    message.Chat.Id,
                    "Informe o numero e complemento, se houver.",
                    KeyboardFactory.CepRequestKeyboard(),
                    context.Session.ActiveJobId,
                    cancellationToken);
                return;
            }

            if (JobWorkflowService.HasCep(numberAndComplement))
            {
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    message.Chat.Id,
                    "Informe somente numero e complemento, sem CEP.",
                    KeyboardFactory.CepRequestKeyboard(),
                    context.Session.ActiveJobId,
                    cancellationToken);
                return;
            }

            if (!numberAndComplement.Any(char.IsDigit))
            {
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    message.Chat.Id,
                    "Informe ao menos o numero do local (ex.: 136, apto 34).",
                    KeyboardFactory.CepRequestKeyboard(),
                    context.Session.ActiveJobId,
                    cancellationToken);
                return;
            }

            context.Draft.AddressText = MergeAddressWithNumber(
                context.Draft.AddressBaseFromCep,
                numberAndComplement,
                context.Draft.Cep);

            var exactGeo = await TryGeocodeByAddressAsync(context.Draft.AddressText, cancellationToken);
            var exactGeoResolved = false;
            if (exactGeo.Success)
            {
                context.Draft.Latitude = exactGeo.Latitude;
                context.Draft.Longitude = exactGeo.Longitude;
                exactGeoResolved = true;
            }
            else
            {
                var fallbackGeo = await TryGeocodeByCepAsync(context.Draft.Cep, cancellationToken);
                if (fallbackGeo.Success)
                {
                    context.Draft.Latitude = fallbackGeo.Latitude;
                    context.Draft.Longitude = fallbackGeo.Longitude;
                }
            }

            context.Draft.WaitingAddressNumber = false;
            context.Draft.WaitingAddressConfirmation = true;

            UserContextService.SaveDraft(context.Session, context.Draft);
            await context.Db.SaveChangesAsync(cancellationToken);

            var geoNote = exactGeoResolved
                ? "Localizacao geocodificada com endereco completo."
                : "Nao localizei o numero exato; vou usar localizacao aproximada do CEP.";
            
            geoNote = "";

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                $"Endereco completo:\n{context.Draft.AddressText}\n\n{geoNote}\n\nEste endereco esta correto?",
                KeyboardFactory.AddressConfirmation(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        if (!IsCepOnlyInput(safeText))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                BotMessages.NeedAddressWithCep(),
                KeyboardFactory.CepRequestKeyboard(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        var cep = ExtractCep(safeText);
        var lookup = await LookupCepAsync(cep, cancellationToken);
        if (!lookup.Ok)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "CEP invalido ou nao encontrado. Envie um CEP valido com 8 digitos.",
                KeyboardFactory.CepRequestKeyboard(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        var baseAddress = BuildAddressFromCep(lookup);
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Nao consegui montar o endereco por esse CEP. Envie outro CEP.",
                KeyboardFactory.CepRequestKeyboard(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        var cepGeo = await TryGeocodeByCepAsync(lookup.Cep, cancellationToken);
        context.Draft.Latitude = cepGeo.Success ? cepGeo.Latitude : null;
        context.Draft.Longitude = cepGeo.Success ? cepGeo.Longitude : null;
        context.Draft.Cep = lookup.Cep;
        context.Draft.AddressBaseFromCep = baseAddress;
        context.Draft.WaitingAddressNumber = true;
        context.Draft.WaitingAddressConfirmation = false;
        context.Draft.AddressText = null;
        UserContextService.SaveDraft(context.Session, context.Draft);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            message.Chat.Id,
            $"Endereco resolvido pelo CEP:\n{baseAddress}\n\nInforme apenas numero e complemento (se houver).",
            KeyboardFactory.CepRequestKeyboard(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task HandleContactNameAsync(
        BotExecutionContext context,
        ChatId chatId,
        string text,
        CancellationToken cancellationToken)
    {
        var safeName = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(safeName) || safeName.Length < 2)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Informe um nome valido para contato (minimo 2 caracteres).",
                null,
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        context.Draft.ContactName = safeName.Length <= 120 ? safeName : safeName[..120];
        UserContextService.SaveDraft(context.Session, context.Draft);
        UserContextService.SetState(context.Session, BotStates.C_CONTACT_PHONE);
        await context.Db.SaveChangesAsync(cancellationToken);

        var suggestedPhone = string.IsNullOrWhiteSpace(context.Draft.ContactPhone)
            ? string.Empty
            : $" (sugestao: {context.Draft.ContactPhone})";
        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            $"{BotMessages.AskContactPhone()}{suggestedPhone}",
            null,
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task HandleContactPhoneAsync(
        BotExecutionContext context,
        Message message,
        string text,
        CancellationToken cancellationToken)
    {
        var phoneInput = message.Contact?.PhoneNumber;
        if (string.IsNullOrWhiteSpace(phoneInput))
        {
            phoneInput = text;
        }

        if (!TryNormalizePhone(phoneInput, out var contactPhone))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Telefone invalido. Informe com DDD (ex.: 13999998888).",
                null,
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        context.Draft.ContactPhone = contactPhone;
        UserContextService.SaveDraft(context.Session, context.Draft);
        UserContextService.SetState(context.Session, BotStates.C_CONFIRM);
        await context.Db.SaveChangesAsync(cancellationToken);

        await SendConfirmationAsync(context, message.Chat.Id, cancellationToken);
    }

    private async Task SendConfirmationAsync(
        BotExecutionContext context,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var summary = JobWorkflowService.BuildConfirmationSummary(context.Draft);
        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            BotMessages.AskConfirm(summary),
            KeyboardFactory.ConfirmRequest(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task HandleHomeCallbackAsync(
        BotExecutionContext context,
        ChatId chatId,
        string action,
        CancellationToken cancellationToken)
    {
        if (context.Session.IsChatActive || string.Equals(context.Session.State, BotStates.CHAT_MEDIATED, StringComparison.Ordinal))
        {
            context.Session.IsChatActive = false;
            context.Session.ChatPeerUserId = null;
            context.Session.ChatJobId = null;
            if (string.Equals(context.Session.State, BotStates.CHAT_MEDIATED, StringComparison.Ordinal))
            {
                context.Session.State = BotStates.C_HOME;
            }

            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
            await context.Db.SaveChangesAsync(cancellationToken);
        }

        switch ((action ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "REQ":
                await StartWizardAsync(context, chatId, cancellationToken);
                return;

            case "MY":
            case "2":
            case "AGENDA":
            case "BOOK":
            case "BOOKINGS":
            case "SCHEDULE":
            case "SCHEDULES":
                try
                {
                    await SendMyJobsAsync(context, chatId, 0, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Falha ao carregar agendamentos do cliente. tenant={Tenant} userId={UserId} telegramUserId={TelegramUserId}",
                        context.TenantId,
                        context.User.Id,
                        context.User.TelegramUserId);

                    await _exceptionLog.TryLogAsync(
                        context.Db,
                        context.TenantId,
                        "ClientFlowHandler.HandleHomeCallbackAsync.C_HOME_MY",
                        ex,
                        context.User.TelegramUserId,
                        context.User.Id,
                        context.Session.ActiveJobId,
                        $"action={action};state={context.Session.State}",
                        cancellationToken);

                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        "Nao consegui carregar seus agendamentos agora. Tente novamente em instantes.",
                        KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                        context.Session.ActiveJobId,
                        cancellationToken);
                }
                return;

            case "FAV":
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Favoritos ainda nao foi configurado para sua conta.",
                    KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                    context.Session.ActiveJobId,
                    cancellationToken);
                return;

            case "HLP":
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Use o menu para pedir servico, acompanhar agendamentos e conversar com o prestador.",
                    KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                    context.Session.ActiveJobId,
                    cancellationToken);
                return;

            case "SWP":
                if (context.User.Role == UserRole.Both)
                {
                    await SwitchToProviderAsync(context, chatId, cancellationToken);
                    return;
                }

                await SendClientHomeMenuAsync(
                    context,
                    chatId,
                    context.Session.ActiveJobId,
                    cancellationToken);
                return;

            case "END":
                await PromptEndAttendanceConfirmationAsync(context, chatId, cancellationToken);
                return;

            default:
                await SendClientHomeMenuAsync(
                    context,
                    chatId,
                    context.Session.ActiveJobId,
                    cancellationToken);
                return;
        }
    }

    private async Task SwitchToProviderAsync(
        BotExecutionContext context,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        UserContextService.SetState(context.Session, BotStates.P_HOME);
        context.Session.ActiveJobId = null;
        context.Session.IsChatActive = false;
        context.Session.ChatPeerUserId = null;
        context.Session.ChatJobId = null;
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            BotMessages.ProviderHomeMenu(context.User.Role == UserRole.Both),
            KeyboardFactory.ProviderMenu(context.User.Role == UserRole.Both),
            null,
            cancellationToken);
    }

    private async Task SendClientHomeMenuAsync(
        BotExecutionContext context,
        ChatId chatId,
        long? relatedJobId,
        CancellationToken cancellationToken)
    {
        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            BotMessages.ClientHomeMenu(context.User.Role == UserRole.Both),
            KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
            relatedJobId,
            cancellationToken);
    }

    private async Task PromptEndAttendanceConfirmationAsync(
        BotExecutionContext context,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var confirmationText = await ResolveConfiguredClientCloseConfirmationTextAsync(context, cancellationToken);
        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            confirmationText,
            KeyboardFactory.ClientEndAttendanceConfirmation(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task CompleteEndAttendanceAsync(
        BotExecutionContext context,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        await CloseOpenHumanHandoffSessionsAsync(context, nowUtc, cancellationToken);

        UserContextService.ResetSession(context.Session, BotStates.C_HOME);
        context.Session.ActiveJobId = null;
        context.Session.UpdatedAt = nowUtc;
        await context.Db.SaveChangesAsync(cancellationToken);

        var closingText = await ResolveConfiguredClientClosingTextAsync(context, cancellationToken);
        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            closingText,
            null,
            null,
            cancellationToken);
    }

    private async Task CloseOpenHumanHandoffSessionsAsync(
        BotExecutionContext context,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var openSessions = await context.Db.HumanHandoffSessions
            .Where(x => x.TenantId == context.TenantId
                        && x.TelegramUserId == context.User.TelegramUserId
                        && x.IsOpen)
            .ToListAsync(cancellationToken);

        foreach (var session in openSessions)
        {
            session.IsOpen = false;
            session.ClosedAtUtc = nowUtc;
            session.CloseReason = "client_closed_attendance";
            session.LastMessageAtUtc = nowUtc;
        }
    }

    private async Task GoHomeAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        context.Session.State = BotStates.C_HOME;
        context.Session.DraftJson = "{}";
        context.Session.IsChatActive = false;
        context.Session.ChatPeerUserId = null;
        context.Session.ChatJobId = null;
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);

        await SendClientHomeMenuAsync(
            context,
            chatId,
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task<string> ResolveConfiguredClientCloseConfirmationTextAsync(
        BotExecutionContext context,
        CancellationToken cancellationToken)
    {
        var configured = await ResolveConfiguredClientMessageTextAsync(
            context,
            static payload => payload.CloseConfirmationText,
            cancellationToken);
        return string.IsNullOrWhiteSpace(configured) ? DefaultCloseConfirmationText : configured;
    }

    private async Task<string> ResolveConfiguredClientClosingTextAsync(
        BotExecutionContext context,
        CancellationToken cancellationToken)
    {
        var configured = await ResolveConfiguredClientMessageTextAsync(
            context,
            static payload => payload.ClosingText,
            cancellationToken);
        return string.IsNullOrWhiteSpace(configured) ? DefaultClosingText : configured;
    }

    private async Task<string> ResolveConfiguredClientMessageTextAsync(
        BotExecutionContext context,
        Func<ClientMessagesConfigStorage, string?> selector,
        CancellationToken cancellationToken)
    {
        var row = await context.Db.TenantBotConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == context.TenantId, cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.MessagesJson))
        {
            return string.Empty;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ClientMessagesConfigStorage>(row.MessagesJson, JsonOptions)
                          ?? new ClientMessagesConfigStorage();
            return selector(payload)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<ClientProfile> GetOrCreateClientProfileAsync(
        BotExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.User.ClientProfile is not null)
        {
            return context.User.ClientProfile;
        }

        var existing = await context.Db.ClientProfiles
            .FirstOrDefaultAsync(x => x.UserId == context.User.Id, cancellationToken);
        if (existing is not null)
        {
            context.User.ClientProfile = existing;
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var created = new ClientProfile
        {
            UserId = context.User.Id,
            TenantId = context.TenantId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        context.Db.ClientProfiles.Add(created);
        context.User.ClientProfile = created;
        return created;
    }

    private static string? ResolveClientRegistrationState(BotExecutionContext context, ClientProfile profile)
    {
        var nextState = DetermineClientRegistrationState(profile);
        if (nextState is null)
        {
            return null;
        }

        if (!string.Equals(context.Session.State, nextState, StringComparison.Ordinal))
        {
            UserContextService.SetState(context.Session, nextState);
        }
        else
        {
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return nextState;
    }

    private static string? DetermineClientRegistrationState(ClientProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.FullName))
        {
            return BotStates.C_REG_NAME;
        }

        if (string.IsNullOrWhiteSpace(profile.Email))
        {
            return BotStates.C_REG_EMAIL;
        }

        if (string.IsNullOrWhiteSpace(profile.Cpf))
        {
            return BotStates.C_REG_CPF;
        }

        if (string.IsNullOrWhiteSpace(profile.Cep)
            || string.IsNullOrWhiteSpace(profile.Street)
            || string.IsNullOrWhiteSpace(profile.Neighborhood)
            || string.IsNullOrWhiteSpace(profile.City)
            || string.IsNullOrWhiteSpace(profile.State))
        {
            return BotStates.C_REG_CEP;
        }

        if (string.IsNullOrWhiteSpace(profile.Number))
        {
            return BotStates.C_REG_ADDRESS_NUMBER;
        }

        if (!profile.IsAddressConfirmed)
        {
            return BotStates.C_REG_ADDRESS_CONFIRM;
        }

        if (string.IsNullOrWhiteSpace(profile.PhoneNumber))
        {
            return BotStates.C_REG_PHONE;
        }

        return null;
    }

    private async Task SendClientRegistrationPromptAsync(
        BotExecutionContext context,
        ChatId chatId,
        ClientProfile profile,
        string? leadMessage,
        CancellationToken cancellationToken)
    {
        var state = ResolveClientRegistrationState(context, profile);
        if (state is null)
        {
            await CompleteClientRegistrationAsync(context, profile, chatId, cancellationToken);
            return;
        }

        var suggestedPhone = NormalizePhoneIfPossible(profile.PhoneNumber)
            ?? NormalizePhoneIfPossible(context.User.Phone);
        var suggestedName = string.IsNullOrWhiteSpace(profile.FullName)
            ? context.User.Name
            : profile.FullName;

        var message = state switch
        {
            BotStates.C_REG_NAME => string.IsNullOrWhiteSpace(suggestedName)
                ? "Cadastro do cliente - 1/6\nInforme seu nome completo."
                : $"Cadastro do cliente - 1/6\nInforme seu nome completo.\nSugestao atual: {suggestedName}",
            BotStates.C_REG_EMAIL => "Cadastro do cliente - 2/6\nInforme seu e-mail.",
            BotStates.C_REG_CPF => "Cadastro do cliente - 3/6\nInforme seu CPF.",
            BotStates.C_REG_CEP => "Cadastro do cliente - 4/6\nEnvie o CEP do seu endereco (8 digitos, com ou sem hifen).",
            BotStates.C_REG_ADDRESS_NUMBER => $"Cadastro do cliente - 5/6\nEndereco resolvido pelo CEP:\n{BuildClientProfileBaseAddress(profile)}\n\nInforme apenas numero e complemento (se houver).",
            BotStates.C_REG_ADDRESS_CONFIRM => $"Cadastro do cliente - 5/6\nConfirme seu endereco:\n{BuildClientProfileFullAddress(profile)}",
            BotStates.C_REG_PHONE => string.IsNullOrWhiteSpace(suggestedPhone)
                ? "Cadastro do cliente - 6/6\nInforme seu telefone com DDD (ex.: 13999998888)."
                : $"Cadastro do cliente - 6/6\nInforme seu telefone com DDD (ex.: 13999998888).\nSugestao atual: {suggestedPhone}",
            _ => "Conclua seu cadastro para continuar."
        };

        if (!string.IsNullOrWhiteSpace(leadMessage))
        {
            message = $"{leadMessage}\n\n{message}";
        }

        IReplyMarkup? keyboard = state switch
        {
            BotStates.C_REG_CEP or BotStates.C_REG_ADDRESS_NUMBER => KeyboardFactory.CepRequestKeyboard(),
            BotStates.C_REG_ADDRESS_CONFIRM => KeyboardFactory.AddressConfirmation(),
            _ => null
        };

        await context.Db.SaveChangesAsync(cancellationToken);
        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            message,
            keyboard,
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task HandleClientRegistrationNameAsync(
        BotExecutionContext context,
        ClientProfile profile,
        ChatId chatId,
        string text,
        CancellationToken cancellationToken)
    {
        var safeName = (text ?? string.Empty).Trim();
        if (safeName.Length < 3)
        {
            await SendClientRegistrationPromptAsync(
                context,
                chatId,
                profile,
                "Informe um nome valido com pelo menos 3 caracteres.",
                cancellationToken);
            return;
        }

        profile.FullName = safeName.Length <= 160 ? safeName : safeName[..160].Trim();
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await ContinueClientRegistrationAsync(context, profile, chatId, cancellationToken);
    }

    private async Task HandleClientRegistrationEmailAsync(
        BotExecutionContext context,
        ClientProfile profile,
        ChatId chatId,
        string text,
        CancellationToken cancellationToken)
    {
        var safeEmail = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidEmail(safeEmail))
        {
            await SendClientRegistrationPromptAsync(
                context,
                chatId,
                profile,
                "Informe um e-mail valido.",
                cancellationToken);
            return;
        }

        profile.Email = safeEmail.Length <= 160 ? safeEmail : safeEmail[..160].Trim();
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await ContinueClientRegistrationAsync(context, profile, chatId, cancellationToken);
    }

    private async Task HandleClientRegistrationCpfAsync(
        BotExecutionContext context,
        ClientProfile profile,
        ChatId chatId,
        string text,
        CancellationToken cancellationToken)
    {
        var normalizedCpf = NormalizeCpf(text);
        if (!IsValidCpf(normalizedCpf))
        {
            await SendClientRegistrationPromptAsync(
                context,
                chatId,
                profile,
                "CPF invalido. Informe um CPF valido com 11 digitos.",
                cancellationToken);
            return;
        }

        profile.Cpf = normalizedCpf;
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await ContinueClientRegistrationAsync(context, profile, chatId, cancellationToken);
    }

    private async Task HandleClientRegistrationCepAsync(
        BotExecutionContext context,
        ClientProfile profile,
        ChatId chatId,
        string text,
        CancellationToken cancellationToken)
    {
        var safeText = (text ?? string.Empty).Trim();
        if (!IsCepOnlyInput(safeText))
        {
            await SendClientRegistrationPromptAsync(
                context,
                chatId,
                profile,
                "Envie apenas um CEP valido com 8 digitos.",
                cancellationToken);
            return;
        }

        var cep = ExtractCep(safeText);
        var lookup = await LookupCepAsync(cep, cancellationToken);
        if (!lookup.Ok || string.IsNullOrWhiteSpace(lookup.Logradouro))
        {
            await SendClientRegistrationPromptAsync(
                context,
                chatId,
                profile,
                "Nao consegui resolver esse CEP com logradouro completo. Envie outro CEP.",
                cancellationToken);
            return;
        }

        var geocode = await TryGeocodeByCepAsync(lookup.Cep, cancellationToken);
        profile.Cep = lookup.Cep;
        profile.Street = lookup.Logradouro.Trim();
        profile.Neighborhood = (lookup.Bairro ?? string.Empty).Trim();
        profile.City = (lookup.Localidade ?? string.Empty).Trim();
        profile.State = (lookup.Uf ?? string.Empty).Trim().ToUpperInvariant();
        profile.Number = string.Empty;
        profile.Complement = string.Empty;
        profile.IsAddressConfirmed = false;
        profile.Latitude = geocode.Success ? geocode.Latitude : null;
        profile.Longitude = geocode.Success ? geocode.Longitude : null;
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await ContinueClientRegistrationAsync(context, profile, chatId, cancellationToken);
    }

    private async Task HandleClientRegistrationAddressNumberAsync(
        BotExecutionContext context,
        ClientProfile profile,
        ChatId chatId,
        string text,
        CancellationToken cancellationToken)
    {
        if (!TryParseClientRegistrationNumber(text, out var number, out var complement))
        {
            await SendClientRegistrationPromptAsync(
                context,
                chatId,
                profile,
                "Informe ao menos o numero do local. Ex.: 136, apto 34.",
                cancellationToken);
            return;
        }

        profile.Number = number;
        profile.Complement = complement;
        profile.IsAddressConfirmed = false;

        var exactAddress = BuildClientProfileFullAddress(profile);
        var exactGeo = await TryGeocodeByAddressAsync(exactAddress, cancellationToken);
        if (exactGeo.Success)
        {
            profile.Latitude = exactGeo.Latitude;
            profile.Longitude = exactGeo.Longitude;
        }
        else
        {
            var fallbackGeo = await TryGeocodeByCepAsync(profile.Cep, cancellationToken);
            profile.Latitude = fallbackGeo.Success ? fallbackGeo.Latitude : profile.Latitude;
            profile.Longitude = fallbackGeo.Success ? fallbackGeo.Longitude : profile.Longitude;
        }

        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await ContinueClientRegistrationAsync(context, profile, chatId, cancellationToken);
    }

    private async Task HandleClientRegistrationAddressConfirmationAsync(
        BotExecutionContext context,
        ClientProfile profile,
        ChatId chatId,
        string action,
        CancellationToken cancellationToken)
    {
        var normalizedAction = (action ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedAction == "EDIT")
        {
            profile.Street = string.Empty;
            profile.Number = string.Empty;
            profile.Complement = string.Empty;
            profile.Neighborhood = string.Empty;
            profile.City = string.Empty;
            profile.State = string.Empty;
            profile.Cep = string.Empty;
            profile.Latitude = null;
            profile.Longitude = null;
            profile.IsAddressConfirmed = false;
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await ContinueClientRegistrationAsync(context, profile, chatId, cancellationToken);
            return;
        }

        if (normalizedAction != "OK")
        {
            await SendClientRegistrationPromptAsync(
                context,
                chatId,
                profile,
                "Use os botoes para confirmar o endereco.",
                cancellationToken);
            return;
        }

        profile.IsAddressConfirmed = true;
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await ContinueClientRegistrationAsync(context, profile, chatId, cancellationToken);
    }

    private async Task HandleClientRegistrationPhoneAsync(
        BotExecutionContext context,
        ClientProfile profile,
        Message message,
        string text,
        CancellationToken cancellationToken)
    {
        var phoneInput = message.Contact?.PhoneNumber;
        if (string.IsNullOrWhiteSpace(phoneInput))
        {
            phoneInput = text;
        }

        if (!TryNormalizePhone(phoneInput, out var phone))
        {
            await SendClientRegistrationPromptAsync(
                context,
                message.Chat.Id,
                profile,
                "Telefone invalido. Informe com DDD.",
                cancellationToken);
            return;
        }

        profile.PhoneNumber = phone;
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await ContinueClientRegistrationAsync(context, profile, message.Chat.Id, cancellationToken);
    }

    private async Task ContinueClientRegistrationAsync(
        BotExecutionContext context,
        ClientProfile profile,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var nextState = ResolveClientRegistrationState(context, profile);
        if (nextState is null)
        {
            await CompleteClientRegistrationAsync(context, profile, chatId, cancellationToken);
            return;
        }

        await context.Db.SaveChangesAsync(cancellationToken);
        await SendClientRegistrationPromptAsync(context, chatId, profile, null, cancellationToken);
    }

    private async Task CompleteClientRegistrationAsync(
        BotExecutionContext context,
        ClientProfile profile,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (profile.CreatedAtUtc == default)
        {
            profile.CreatedAtUtc = now;
        }

        profile.TenantId = context.TenantId;
        profile.IsAddressConfirmed = true;
        profile.IsRegistrationComplete = true;
        profile.UpdatedAtUtc = now;

        context.User.Name = profile.FullName;
        context.User.Phone = profile.PhoneNumber;
        context.User.UpdatedAt = now;
        context.User.ClientProfile = profile;

        UserContextService.ResetSession(context.Session, BotStates.C_HOME);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            $"Cadastro concluido com sucesso.\n\nNome: {profile.FullName}\nCPF: {FormatCpf(profile.Cpf)}\nE-mail: {profile.Email}\nTelefone: {profile.PhoneNumber}\nEndereco: {BuildClientProfileFullAddress(profile)}",
            KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
            null,
            cancellationToken);
    }

    private async Task AdvanceToScheduleAsync(
        BotExecutionContext context,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        context.Draft.WaitingAddressNumber = false;
        context.Draft.WaitingAddressConfirmation = false;
        context.Draft.AddressBaseFromCep = null;
        UserContextService.SaveDraft(context.Session, context.Draft);
        UserContextService.SetState(context.Session, BotStates.C_SCHEDULE);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            BotMessages.AskSchedule(),
            KeyboardFactory.ScheduleMode(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task<AvailabilityRequest> BuildAvailabilityRequestAsync(
        BotExecutionContext context,
        long clientUserId,
        long? providerUserId,
        long? excludeJobId,
        bool requireFutureSlotsOnly,
        CancellationToken cancellationToken)
    {
        var rules = _availability is null
            ? AvailabilityRules.Default
            : await _availability.GetRulesAsync(context.Db, context.TenantId, cancellationToken);

        return new AvailabilityRequest
        {
            TenantId = context.TenantId,
            ClientUserId = clientUserId,
            ProviderUserId = providerUserId,
            ExcludeJobId = excludeJobId,
            Rules = rules,
            TimeZone = context.Runtime.TimeZone,
            NowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, context.Runtime.TimeZone),
            RequireFutureSlotsOnly = requireFutureSlotsOnly
        };
    }

    private static bool IsCepOnlyInput(string text)
    {
        var safe = (text ?? string.Empty).Trim();
        if (safe.Length == 0)
        {
            return false;
        }

        foreach (var ch in safe)
        {
            if (!char.IsDigit(ch) && ch != '-' && !char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        var digits = new string(safe.Where(char.IsDigit).ToArray());
        return digits.Length == 8;
    }

    private static bool IsClientRegistrationComplete(ClientProfile? profile)
    {
        return profile is not null
            && profile.IsRegistrationComplete
            && !string.IsNullOrWhiteSpace(profile.FullName)
            && !string.IsNullOrWhiteSpace(profile.Email)
            && !string.IsNullOrWhiteSpace(profile.Cpf)
            && !string.IsNullOrWhiteSpace(profile.Street)
            && !string.IsNullOrWhiteSpace(profile.Number)
            && !string.IsNullOrWhiteSpace(profile.Neighborhood)
            && !string.IsNullOrWhiteSpace(profile.City)
            && !string.IsNullOrWhiteSpace(profile.State)
            && !string.IsNullOrWhiteSpace(profile.Cep)
            && !string.IsNullOrWhiteSpace(profile.PhoneNumber)
            && profile.IsAddressConfirmed;
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        try
        {
            var mail = new MailAddress(email);
            return string.Equals(mail.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeCpf(string? value)
        => ClientRegistrationCpfRegex.Replace(value ?? string.Empty, string.Empty);

    private static string FormatCpf(string? cpf)
    {
        var digits = NormalizeCpf(cpf);
        return digits.Length == 11
            ? $"{digits[..3]}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits[9..]}"
            : digits;
    }

    private static bool IsValidCpf(string? cpf)
    {
        var digits = NormalizeCpf(cpf);
        if (digits.Length != 11)
        {
            return false;
        }

        if (digits.Distinct().Count() == 1)
        {
            return false;
        }

        static int CalculateDigit(string source, int factor)
        {
            var sum = 0;
            foreach (var ch in source)
            {
                sum += (ch - '0') * factor--;
            }

            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        var digit1 = CalculateDigit(digits[..9], 10);
        var digit2 = CalculateDigit(digits[..10], 11);
        return digits[9] - '0' == digit1 && digits[10] - '0' == digit2;
    }

    private static bool TryParseClientRegistrationNumber(string? text, out string number, out string complement)
    {
        number = string.Empty;
        complement = string.Empty;

        var safe = (text ?? string.Empty).Trim();
        if (safe.Length == 0 || !safe.Any(char.IsDigit))
        {
            return false;
        }

        var match = ClientRegistrationNumberRegex.Match(safe);
        if (match.Success)
        {
            number = match.Groups["number"].Value.Trim();
            complement = match.Groups["complement"].Value.Trim().Trim(',', '-', '/').Trim();
            return !string.IsNullOrWhiteSpace(number);
        }

        number = safe;
        return true;
    }

    private static string BuildClientProfileBaseAddress(ClientProfile profile)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.Street))
        {
            parts.Add(profile.Street.Trim());
        }

        if (!string.IsNullOrWhiteSpace(profile.Neighborhood))
        {
            parts.Add(profile.Neighborhood.Trim());
        }

        var cityUf = BuildCityUf(profile.City, profile.State);
        if (!string.IsNullOrWhiteSpace(cityUf))
        {
            parts.Add(cityUf);
        }

        var assembled = string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(profile.Cep)
            ? assembled
            : string.IsNullOrWhiteSpace(assembled)
                ? $"CEP {FormatCep(profile.Cep)}"
                : $"{assembled}, CEP {FormatCep(profile.Cep)}";
    }

    private static string BuildClientProfileFullAddress(ClientProfile profile)
    {
        var parts = new List<string>();
        var firstLine = new List<string>();

        if (!string.IsNullOrWhiteSpace(profile.Street))
        {
            firstLine.Add(profile.Street.Trim());
        }

        if (!string.IsNullOrWhiteSpace(profile.Number))
        {
            firstLine.Add(profile.Number.Trim());
        }

        if (!string.IsNullOrWhiteSpace(profile.Complement))
        {
            firstLine.Add(profile.Complement.Trim());
        }

        if (firstLine.Count > 0)
        {
            parts.Add(string.Join(", ", firstLine));
        }

        if (!string.IsNullOrWhiteSpace(profile.Neighborhood))
        {
            parts.Add(profile.Neighborhood.Trim());
        }

        var cityUf = BuildCityUf(profile.City, profile.State);
        if (!string.IsNullOrWhiteSpace(cityUf))
        {
            parts.Add(cityUf);
        }

        if (!string.IsNullOrWhiteSpace(profile.Cep))
        {
            parts.Add($"CEP {FormatCep(profile.Cep)}");
        }

        return string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string BuildAddressFromCep(CepLookupResult lookup)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(lookup.Logradouro))
        {
            parts.Add(lookup.Logradouro.Trim());
        }

        if (!string.IsNullOrWhiteSpace(lookup.Bairro))
        {
            parts.Add(lookup.Bairro.Trim());
        }

        var cityUf = BuildCityUf(lookup.Localidade, lookup.Uf);
        if (!string.IsNullOrWhiteSpace(cityUf))
        {
            parts.Add(cityUf);
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var assembled = string.Join(", ", parts);
        if (!string.IsNullOrWhiteSpace(lookup.Cep))
        {
            assembled = string.IsNullOrWhiteSpace(assembled)
                ? $"CEP {FormatCep(lookup.Cep)}"
                : $"{assembled}, CEP {FormatCep(lookup.Cep)}";
        }

        return assembled;
    }

    private static string MergeAddressWithNumber(string? baseAddress, string numberAndComplement, string? cep)
    {
        var trimmedBase = RemoveTrailingCep((baseAddress ?? string.Empty).Trim());
        var numberPart = (numberAndComplement ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(numberPart))
        {
            return string.IsNullOrWhiteSpace(trimmedBase) ? string.Empty : trimmedBase;
        }

        string merged;
        var firstComma = trimmedBase.IndexOf(',');
        if (firstComma > 0)
        {
            var firstPart = trimmedBase[..firstComma].Trim();
            var rest = trimmedBase[(firstComma + 1)..].Trim();
            merged = string.IsNullOrWhiteSpace(rest)
                ? $"{firstPart}, {numberPart}"
                : $"{firstPart}, {numberPart}, {rest}";
        }
        else if (!string.IsNullOrWhiteSpace(trimmedBase))
        {
            merged = $"{trimmedBase}, {numberPart}";
        }
        else
        {
            merged = numberPart;
        }

        return string.IsNullOrWhiteSpace(cep)
            ? merged
            : $"{merged}, CEP {FormatCep(cep)}";
    }

    private static string RemoveTrailingCep(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var marker = text.LastIndexOf(", CEP ", StringComparison.OrdinalIgnoreCase);
        return marker > -1 ? text[..marker].Trim() : text.Trim();
    }

    private static string BuildCityUf(string? localidade, string? uf)
    {
        var city = (localidade ?? string.Empty).Trim();
        var state = (uf ?? string.Empty).Trim().ToUpperInvariant();
        if (city.Length > 0 && state.Length > 0)
        {
            return $"{city} - {state}";
        }

        return city.Length > 0 ? city : state;
    }

    private static string FormatCep(string cep)
    {
        var digits = new string((cep ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length != 8)
        {
            return cep ?? string.Empty;
        }

        return $"{digits[..5]}-{digits[5..]}";
    }

    private static async Task<CepLookupResult> LookupCepAsync(string cep, CancellationToken cancellationToken)
    {
        var digits = new string((cep ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length != 8)
        {
            return CepLookupResult.Fail("CEP invalido.");
        }

        var viaCep = await TryLookupViaCepAsync(digits, cancellationToken);
        if (viaCep.Ok && !string.IsNullOrWhiteSpace(viaCep.Logradouro))
        {
            return viaCep;
        }

        var brasilApi = await TryLookupBrasilApiAsync(digits, cancellationToken);
        if (brasilApi.Ok && !string.IsNullOrWhiteSpace(brasilApi.Logradouro))
        {
            return brasilApi;
        }

        if (viaCep.Ok)
        {
            return viaCep;
        }

        if (brasilApi.Ok)
        {
            return brasilApi;
        }

        return CepLookupResult.Fail(!string.IsNullOrWhiteSpace(brasilApi.Error) ? brasilApi.Error : viaCep.Error);
    }

    private static async Task<CepLookupResult> TryLookupViaCepAsync(string digits, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await ViaCepHttpClient.GetAsync($"https://viacep.com.br/ws/{digits}/json/", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CepLookupResult.Fail($"HTTP {(int)response.StatusCode} no ViaCEP.");
            }

            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<ViaCepPayload>(payloadText, JsonOptions);
            if (payload is null || payload.Erro)
            {
                return CepLookupResult.Fail("CEP nao encontrado.");
            }

            return CepLookupResult.Success(
                digits,
                payload.Logradouro,
                payload.Bairro,
                payload.Localidade,
                payload.Uf);
        }
        catch (Exception ex)
        {
            return CepLookupResult.Fail(ex.Message);
        }
    }

    private static async Task<CepLookupResult> TryLookupBrasilApiAsync(string digits, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await ViaCepHttpClient.GetAsync($"https://brasilapi.com.br/api/cep/v2/{digits}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CepLookupResult.Fail($"HTTP {(int)response.StatusCode} na BrasilAPI.");
            }

            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<BrasilApiCepPayload>(payloadText, JsonOptions);
            if (payload is null)
            {
                return CepLookupResult.Fail("BrasilAPI retornou payload invalido.");
            }

            return CepLookupResult.Success(
                digits,
                payload.Street,
                payload.Neighborhood,
                payload.City,
                payload.State);
        }
        catch (Exception ex)
        {
            return CepLookupResult.Fail(ex.Message);
        }
    }

    private static Task<GeocodeResult> TryGeocodeByCepAsync(string? cep, CancellationToken cancellationToken)
    {
        var digits = new string((cep ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length != 8)
        {
            return Task.FromResult(GeocodeResult.Fail("CEP invalido para geocode."));
        }

        return TryGeocodeByCepInternalAsync(digits, cancellationToken);
    }

    private static async Task<GeocodeResult> TryGeocodeByCepInternalAsync(string cepDigits, CancellationToken cancellationToken)
    {
        var awesome = await TryGeocodeByAwesomeCepAsync(cepDigits, cancellationToken);
        if (awesome.Success)
        {
            return awesome;
        }

        var viaCep = await LookupCepAsync(cepDigits, cancellationToken);
        if (viaCep.Ok)
        {
            var baseAddress = BuildAddressFromCep(viaCep);
            var byAddress = await TryGeocodeByAddressCandidatesAsync(baseAddress, cancellationToken, allowCepFallback: false);
            if (byAddress.Success)
            {
                return byAddress;
            }
        }

        return await TryGeocodeQueryAsync($"{cepDigits}, Brasil", cancellationToken);
    }

    private static Task<GeocodeResult> TryGeocodeByAddressAsync(string? address, CancellationToken cancellationToken)
    {
        var safe = (address ?? string.Empty).Trim();
        if (safe.Length == 0)
        {
            return Task.FromResult(GeocodeResult.Fail("Endereco vazio para geocode."));
        }

        return TryGeocodeByAddressCandidatesAsync(safe, cancellationToken, allowCepFallback: true);
    }

    private static async Task<GeocodeResult> TryGeocodeByAddressCandidatesAsync(string address, CancellationToken cancellationToken, bool allowCepFallback)
    {
        if (allowCepFallback && TryExtractCepFromText(address, out var cepDigits))
        {
            var cepResult = await TryGeocodeByCepInternalAsync(cepDigits, cancellationToken);
            if (cepResult.Success)
            {
                return cepResult;
            }
        }

        string? lastError = null;
        foreach (var candidate in BuildAddressGeocodeCandidates(address))
        {
            var result = await TryGeocodeQueryAsync($"{candidate}, Brasil", cancellationToken);
            if (result.Success)
            {
                return result;
            }

            var photonFallback = await TryPhotonGeocodeQueryAsync($"{candidate}, Brasil", cancellationToken);
            if (photonFallback.Success)
            {
                return photonFallback;
            }

            lastError = photonFallback.Error ?? result.Error;
            if (IsGeocodeRateLimited(result.Error))
            {
                break;
            }
        }

        return GeocodeResult.Fail(lastError ?? "Nenhum resultado no geocode.");
    }

    private static async Task<GeocodeResult> TryGeocodeQueryAsync(string query, CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(query);
        var endpoint = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=br&q={encoded}";

        try
        {
            using var response = await SendThrottledNominatimRequestAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GeocodeResult.Fail($"HTTP {(int)response.StatusCode} no geocode.");
            }

            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payloadText);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() == 0)
            {
                return GeocodeResult.Fail("Nenhum resultado no geocode.");
            }

            var first = document.RootElement[0];
            var latText = first.TryGetProperty("lat", out var latProp) ? latProp.GetString() : null;
            var lonText = first.TryGetProperty("lon", out var lonProp) ? lonProp.GetString() : null;
            if (!double.TryParse(latText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(lonText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lng))
            {
                return GeocodeResult.Fail("Lat/lng invalidos no geocode.");
            }

            return GeocodeResult.Ok(lat, lng);
        }
        catch (Exception ex)
        {
            return GeocodeResult.Fail(ex.Message);
        }
    }

    private static async Task<GeocodeResult> TryPhotonGeocodeQueryAsync(string query, CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(query);
        var endpoint = $"https://photon.komoot.io/api/?limit=1&q={encoded}";

        try
        {
            using var response = await GeocodeHttpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GeocodeResult.Fail($"Photon HTTP {(int)response.StatusCode}.");
            }

            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payloadText);
            if (!document.RootElement.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Array
                || features.GetArrayLength() == 0)
            {
                return GeocodeResult.Fail("Photon sem resultado.");
            }

            var first = features[0];
            if (!first.TryGetProperty("geometry", out var geometry)
                || !geometry.TryGetProperty("coordinates", out var coordinates)
                || coordinates.ValueKind != JsonValueKind.Array
                || coordinates.GetArrayLength() < 2)
            {
                return GeocodeResult.Fail("Photon payload invalido.");
            }

            var lng = coordinates[0].GetDouble();
            var lat = coordinates[1].GetDouble();
            return GeocodeResult.Ok(lat, lng);
        }
        catch (Exception ex)
        {
            return GeocodeResult.Fail(ex.Message);
        }
    }

    private static async Task<HttpResponseMessage> SendThrottledNominatimRequestAsync(string endpoint, CancellationToken cancellationToken)
    {
        await NominatimThrottle.WaitAsync(cancellationToken);
        try
        {
            var wait = (LastNominatimRequestUtc + TimeSpan.FromMilliseconds(1200)) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            var response = await GeocodeHttpClient.GetAsync(endpoint, cancellationToken);
            LastNominatimRequestUtc = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            NominatimThrottle.Release();
        }
    }

    private static async Task<GeocodeResult> TryGeocodeByAwesomeCepAsync(string cepDigits, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cepDigits) || cepDigits.Length != 8)
        {
            return GeocodeResult.Fail("CEP invalido para AwesomeAPI.");
        }

        var endpoint = $"https://cep.awesomeapi.com.br/json/{cepDigits}";
        try
        {
            using var response = await GeocodeHttpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GeocodeResult.Fail($"HTTP {(int)response.StatusCode} no AwesomeAPI.");
            }

            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<AwesomeCepPayload>(payloadText, JsonOptions);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Lat)
                || string.IsNullOrWhiteSpace(payload.Lng))
            {
                return GeocodeResult.Fail("AwesomeAPI sem lat/lng.");
            }

            if (!double.TryParse(
                    payload.Lat.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var lat)
                || !double.TryParse(
                    payload.Lng.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var lng))
            {
                return GeocodeResult.Fail("AwesomeAPI retornou lat/lng invalidos.");
            }

            return GeocodeResult.Ok(lat, lng);
        }
        catch (Exception ex)
        {
            return GeocodeResult.Fail(ex.Message);
        }
    }

    private static HttpClient BuildViaCepHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BotAgendamentoAI.Telegram/1.0 (+lookup-cep)");
        return client;
    }

    private static HttpClient BuildGeocodeHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BotAgendamentoAI.Telegram/1.0 (+nominatim-geocode)");
        return client;
    }

    private static string ExtractCep(string text)
    {
        var digits = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8)
        {
            return string.Empty;
        }

        return digits[^8..];
    }

    private static bool TryExtractCepFromText(string? text, out string cepDigits)
    {
        cepDigits = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Regex.Match(text, @"\b\d{5}-?\d{3}\b");
        if (!match.Success)
        {
            return false;
        }

        var digits = new string(match.Value.Where(char.IsDigit).ToArray());
        if (digits.Length != 8)
        {
            return false;
        }

        cepDigits = digits;
        return true;
    }

    private static bool IsGeocodeRateLimited(string? error)
        => !string.IsNullOrWhiteSpace(error)
           && error.Contains("429", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseSchedule(string yyyymmdd, string hhmm, TimeZoneInfo tz, out DateTimeOffset result)
    {
        result = default;
        if (yyyymmdd?.Length != 8 || hhmm?.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(yyyymmdd[..4], out var year)
            || !int.TryParse(yyyymmdd.Substring(4, 2), out var month)
            || !int.TryParse(yyyymmdd.Substring(6, 2), out var day)
            || !int.TryParse(hhmm[..2], out var hour)
            || !int.TryParse(hhmm.Substring(2, 2), out var minute))
        {
            return false;
        }

        DateTime local;
        try
        {
            local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }
        catch
        {
            return false;
        }

        var offset = tz.GetUtcOffset(local);
        result = new DateTimeOffset(local, offset);
        return true;
    }

    private static bool CanOpenChat(JobStatus status, bool hasProvider)
    {
        if (!hasProvider)
        {
            return false;
        }

        return status is JobStatus.Accepted or JobStatus.OnTheWay or JobStatus.Arrived or JobStatus.InProgress;
    }

    private static bool CanCancelClientJob(JobStatus status)
        => status is not JobStatus.Cancelled and not JobStatus.Finished;

    private static bool CanRescheduleClientJob(JobStatus status)
        => status is JobStatus.WaitingProvider or JobStatus.Requested or JobStatus.Accepted;

    private static InlineKeyboardMarkup? BuildClientJobActions(Job job)
    {
        var rows = new List<InlineKeyboardButton[]>();
        var managementButtons = new List<InlineKeyboardButton>();

        if (CanRescheduleClientJob(job.Status))
        {
            managementButtons.Add(InlineKeyboardButton.WithCallbackData("Re-agendar", $"J:{job.Id}:RS:CAL"));
        }

        if (CanCancelClientJob(job.Status))
        {
            managementButtons.Add(InlineKeyboardButton.WithCallbackData("Cancelar Pedido", $"J:{job.Id}:CAN"));
        }

        if (managementButtons.Count > 0)
        {
            rows.Add(managementButtons.ToArray());
        }

        if (CanOpenChat(job.Status, job.ProviderUserId.HasValue))
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("Abrir chat", $"J:{job.Id}:CHAT")
            });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData(MenuTexts.EndAttendance, CallbackDataRouter.ClientEndAttendanceRequest())
        });

        return rows.Count == 0 ? null : new InlineKeyboardMarkup(rows);
    }

    private async Task HandleClientJobCancelAsync(
        BotExecutionContext context,
        ChatId chatId,
        long jobId,
        CancellationToken cancellationToken)
    {
        var job = await context.Db.Jobs
            .FirstOrDefaultAsync(x => x.Id == jobId && x.ClientUserId == context.User.Id, cancellationToken);
        if (job is null)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Nao encontrei esse agendamento para cancelar.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        if (!CanCancelClientJob(job.Status))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                $"Nao e possivel cancelar o agendamento #{job.Id} no status atual ({FormatJobStatus(job.Status)}).",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                job.Id,
                cancellationToken);
            return;
        }

        job.Status = JobStatus.Cancelled;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        if (context.Session.ActiveJobId == job.Id)
        {
            context.Session.IsChatActive = false;
            context.Session.ChatPeerUserId = null;
            context.Session.ChatJobId = null;
            context.Session.State = BotStates.C_TRACKING;
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await context.Db.SaveChangesAsync(cancellationToken);

        if (_calendarQueue is not null)
        {
            await _calendarQueue.EnqueueCancelAsync(context.Db, job, "client_cancelled", cancellationToken);
        }

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            $"Agendamento #{job.Id} cancelado com sucesso.",
            KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
            job.Id,
            cancellationToken);
    }

    private async Task HandleClientJobRescheduleAsync(
        BotExecutionContext context,
        ChatId chatId,
        long jobId,
        CallbackRoute route,
        CancellationToken cancellationToken)
    {
        var job = await context.Db.Jobs
            .FirstOrDefaultAsync(x => x.Id == jobId && x.ClientUserId == context.User.Id, cancellationToken);
        if (job is null)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Nao encontrei esse agendamento para reagendar.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        if (!CanRescheduleClientJob(job.Status))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                $"Nao e possivel reagendar o agendamento #{job.Id} no status atual ({FormatJobStatus(job.Status)}).",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                job.Id,
                cancellationToken);
            return;
        }

        if (route.Arg2 == "CAL")
        {
            InlineKeyboardMarkup dayKeyboard;
            if (_availability is not null)
            {
                var request = await BuildAvailabilityRequestAsync(
                    context,
                    context.User.Id,
                    job.ProviderUserId,
                    job.Id,
                    true,
                    cancellationToken);
                var days = await _availability.GetAvailableDaysAsync(context.Db, request, cancellationToken);
                if (days.Count == 0)
                {
                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        "Nao ha horarios disponiveis para reagendamento nos proximos dias.",
                        KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                        job.Id,
                        cancellationToken);
                    return;
                }

                dayKeyboard = KeyboardFactory.ClientRescheduleDaySelection(job.Id, days);
            }
            else
            {
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, context.Runtime.TimeZone);
                dayKeyboard = KeyboardFactory.ClientRescheduleDaySelection(job.Id, nowLocal);
            }

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                $"Selecione o novo dia para o agendamento #{job.Id}:",
                dayKeyboard,
                job.Id,
                cancellationToken);
            return;
        }

        if (route.Arg2 == "DAY")
        {
            var dayToken = route.Arg3;
            if (string.IsNullOrWhiteSpace(dayToken) || dayToken.Length != 8 || !dayToken.All(char.IsDigit))
            {
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Dia invalido para reagendamento.",
                    KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                    job.Id,
                    cancellationToken);
                return;
            }

            if (_availability is not null)
            {
                var request = await BuildAvailabilityRequestAsync(
                    context,
                    context.User.Id,
                    job.ProviderUserId,
                    job.Id,
                    true,
                    cancellationToken);
                var slots = await _availability.GetAvailableTimeSlotsAsync(
                    context.Db,
                    request,
                    dayToken,
                    cancellationToken);
                if (slots.Count == 0)
                {
                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        "Esse dia esta sem horarios livres para reagendamento.",
                        KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                        job.Id,
                        cancellationToken);
                    return;
                }

                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    $"Agora selecione o novo horario para o agendamento #{job.Id}:",
                    KeyboardFactory.ClientRescheduleTimeSelection(job.Id, dayToken, slots),
                    job.Id,
                    cancellationToken);
                return;
            }

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                $"Agora selecione o novo horario para o agendamento #{job.Id}:",
                KeyboardFactory.ClientRescheduleTimeSelection(job.Id, dayToken),
                job.Id,
                cancellationToken);
            return;
        }

        if (route.Arg2 == "TIM")
        {
            var token = (route.Arg3 ?? string.Empty).Trim();
            var digits = new string(token.Where(char.IsDigit).ToArray());
            if (digits.Length != 12)
            {
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Horario invalido para reagendamento.",
                    KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                    job.Id,
                    cancellationToken);
                return;
            }

            var yyyymmdd = digits[..8];
            var hhmm = digits[8..];
            if (!TryParseSchedule(yyyymmdd, hhmm, context.Runtime.TimeZone, out var scheduledAt))
            {
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Nao consegui interpretar a nova data/hora.",
                    KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                    job.Id,
                    cancellationToken);
                return;
            }

            if (_availability is not null)
            {
                var request = await BuildAvailabilityRequestAsync(
                    context,
                    context.User.Id,
                    job.ProviderUserId,
                    job.Id,
                    true,
                    cancellationToken);
                var check = await _availability.CheckSlotAvailabilityAsync(
                    context.Db,
                    request,
                    scheduledAt,
                    cancellationToken);
                if (!check.IsAvailable)
                {
                    var slots = await _availability.GetAvailableTimeSlotsAsync(
                        context.Db,
                        request,
                        yyyymmdd,
                        cancellationToken);
                    var keyboard = slots.Count > 0
                        ? KeyboardFactory.ClientRescheduleTimeSelection(job.Id, yyyymmdd, slots)
                        : KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both);
                    await _sender.SendTextAsync(
                        context.Db,
                        context.Bot,
                        context.TenantId,
                        context.User.TelegramUserId,
                        chatId,
                        "Esse horario nao esta mais disponivel para reagendamento.",
                        keyboard,
                        job.Id,
                        cancellationToken);
                    return;
                }
            }

            job.IsUrgent = false;
            job.ScheduledAt = scheduledAt;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            context.Session.ActiveJobId = job.Id;
            context.Session.State = BotStates.C_TRACKING;
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
            await context.Db.SaveChangesAsync(cancellationToken);

            if (_calendarQueue is not null)
            {
                await _calendarQueue.EnqueueUpsertAsync(context.Db, job, "client_rescheduled", cancellationToken);
            }

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                $"Agendamento #{job.Id} reagendado para {TimeZoneInfo.ConvertTime(scheduledAt, context.Runtime.TimeZone):dd/MM/yyyy HH:mm}.",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                job.Id,
                cancellationToken);
            return;
        }

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            "Opcao de reagendamento invalida.",
            KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
            job.Id,
            cancellationToken);
    }

    private static string FormatJobStatus(JobStatus status)
    {
        return status switch
        {
            JobStatus.Draft => "Rascunho",
            JobStatus.Requested => "Solicitado",
            JobStatus.WaitingProvider => "Aguardando prestador",
            JobStatus.Accepted => "Aceito",
            JobStatus.OnTheWay => "A caminho",
            JobStatus.Arrived => "Prestador chegou",
            JobStatus.InProgress => "Em andamento",
            JobStatus.Finished => "Finalizado",
            JobStatus.Cancelled => "Cancelado",
            _ => status.ToString()
        };
    }

    private async Task SendCategorySelectionAsync(
        BotExecutionContext context,
        ChatId chatId,
        string? leadMessage,
        CancellationToken cancellationToken)
    {
        var categories = await _jobWorkflow.GetCategoriesAsync(context.Db, context.TenantId, cancellationToken);
        var message = string.IsNullOrWhiteSpace(leadMessage)
            ? BotMessages.AskCategory()
            : $"{leadMessage}\n\n{BotMessages.AskCategory()}";

        UserContextService.SetState(context.Session, BotStates.C_PICK_CATEGORY);
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            message,
            KeyboardFactory.Categories(categories),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private static string NormalizeCategoryKey(string? value)
    {
        var safe = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (safe.Length == 0)
        {
            return string.Empty;
        }

        return safe
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
    }

    private static string? NormalizePhoneIfPossible(string? rawPhone)
    {
        return TryNormalizePhone(rawPhone, out var normalized)
            ? normalized
            : null;
    }

    private static bool TryNormalizePhone(string? rawPhone, out string normalizedPhone)
    {
        normalizedPhone = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPhone))
        {
            return false;
        }

        var trimmed = rawPhone.Trim();
        var hasPlusPrefix = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length is < 10 or > 15)
        {
            return false;
        }

        normalizedPhone = hasPlusPrefix ? $"+{digits}" : digits;
        return true;
    }

    private static string NormalizeClientMenuInput(string text, string state)
    {
        if (!IsClientMenuState(state))
        {
            return text;
        }

        var safe = (text ?? string.Empty).Trim();
        var menuNumber = TryGetLeadingMenuNumber(safe);
        if (menuNumber.HasValue)
        {
            return menuNumber.Value switch
            {
                1 => MenuTexts.ClientRequestService,
                2 => MenuTexts.ClientMyBookings,
                3 => MenuTexts.ClientFavorites,
                4 => MenuTexts.ClientHelp,
                5 => MenuTexts.ClientSwitchToProvider,
                6 => MenuTexts.EndAttendance,
                _ => safe
            };
        }

        return safe switch
        {
            MenuTexts.ClientRequestService => MenuTexts.ClientRequestService,
            MenuTexts.ClientMyBookings => MenuTexts.ClientMyBookings,
            MenuTexts.ClientFavorites => MenuTexts.ClientFavorites,
            MenuTexts.ClientHelp => MenuTexts.ClientHelp,
            MenuTexts.ClientSwitchToProvider => MenuTexts.ClientSwitchToProvider,
            MenuTexts.EndAttendance => MenuTexts.EndAttendance,
            _ => safe
        };
    }

    private static int? TryGetLeadingMenuNumber(string text)
    {
        var safe = (text ?? string.Empty).Trim();
        if (safe.Length == 0 || !char.IsDigit(safe[0]))
        {
            return null;
        }

        var index = 0;
        while (index < safe.Length && char.IsDigit(safe[index]))
        {
            index++;
        }

        if (!int.TryParse(safe[..index], out var parsed))
        {
            return null;
        }

        if (index == safe.Length)
        {
            return parsed;
        }

        var separator = safe[index];
        return separator is ' ' or '-' or '.' or ')' or ':' ? parsed : null;
    }

    private static bool IsClientMenuState(string state)
        => string.Equals(state, BotStates.C_HOME, StringComparison.Ordinal)
           || string.Equals(state, BotStates.C_TRACKING, StringComparison.Ordinal)
           || string.Equals(state, BotStates.NONE, StringComparison.Ordinal);

    private sealed class ViaCepPayload
    {
        public string? Cep { get; set; }
        public string? Logradouro { get; set; }
        public string? Bairro { get; set; }
        public string? Localidade { get; set; }
        public string? Uf { get; set; }
        public bool Erro { get; set; }
    }

    private sealed class CepLookupResult
    {
        public bool Ok { get; private set; }
        public string Cep { get; private set; } = string.Empty;
        public string Logradouro { get; private set; } = string.Empty;
        public string Bairro { get; private set; } = string.Empty;
        public string Localidade { get; private set; } = string.Empty;
        public string Uf { get; private set; } = string.Empty;
        public string Error { get; private set; } = string.Empty;

        public static CepLookupResult Success(
            string cep,
            string? logradouro,
            string? bairro,
            string? localidade,
            string? uf)
        {
            return new CepLookupResult
            {
                Ok = true,
                Cep = cep ?? string.Empty,
                Logradouro = logradouro ?? string.Empty,
                Bairro = bairro ?? string.Empty,
                Localidade = localidade ?? string.Empty,
                Uf = uf ?? string.Empty,
                Error = string.Empty
            };
        }

        public static CepLookupResult Fail(string error)
        {
            return new CepLookupResult
            {
                Ok = false,
                Error = error ?? string.Empty
            };
        }
    }

    private sealed class AwesomeCepPayload
    {
        public string? Cep { get; set; }
        public string? Lat { get; set; }
        public string? Lng { get; set; }
    }

    private sealed class BrasilApiCepPayload
    {
        public string? Cep { get; set; }
        public string? Street { get; set; }
        public string? Neighborhood { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
    }

    private sealed class GeocodeResult
    {
        public bool Success { get; private set; }
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        public string Error { get; private set; } = string.Empty;

        public static GeocodeResult Ok(double latitude, double longitude)
        {
            return new GeocodeResult
            {
                Success = true,
                Latitude = latitude,
                Longitude = longitude,
                Error = string.Empty
            };
        }

        public static GeocodeResult Fail(string error)
        {
            return new GeocodeResult
            {
                Success = false,
                Error = error ?? string.Empty
            };
        }
    }

    private static IReadOnlyList<string> BuildAddressGeocodeCandidates(string rawAddress)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string NormalizeCandidate(string value)
        {
            var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s*,\s*", ", ");
            normalized = Regex.Replace(normalized, @",\s*,+", ", ");
            return normalized.Trim().Trim(',');
        }

        void Add(string value)
        {
            var normalized = NormalizeCandidate(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (seen.Add(normalized))
            {
                output.Add(normalized);
            }
        }

        var withoutCep = Regex.Replace(
            rawAddress,
            @"(?i),?\s*CEP\s*\d{5}-?\d{3}\b",
            string.Empty);
        withoutCep = Regex.Replace(
            withoutCep,
            @"(?i),?\s*\b\d{5}-?\d{3}\b",
            string.Empty);

        Add(rawAddress);
        Add(withoutCep);

        var withoutComplement = Regex.Replace(
            withoutCep,
            @"(?i),?\s*(apto|apt|apartamento|bloco|casa|fundos|sala|conjunto|complemento)\b[^,]*",
            string.Empty);
        Add(withoutComplement);

        var withoutNumber = RemoveHouseNumberSegment(withoutComplement);
        Add(withoutNumber);
        Add(withoutNumber.Replace(" - ", ", ", StringComparison.Ordinal));

        var parts = withoutNumber
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length >= 3)
        {
            Add(string.Join(", ", parts.TakeLast(3)));
            Add($"{parts[0]}, {parts[^2]}, {parts[^1]}");
        }

        if (parts.Length >= 2)
        {
            Add(string.Join(", ", parts.TakeLast(2)));
        }

        return output;
    }

    private static string RemoveHouseNumberSegment(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var parts = address
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        if (parts.Count < 2)
        {
            return address;
        }

        for (var i = 1; i < parts.Count; i++)
        {
            var part = parts[i];
            if (Regex.IsMatch(part, @"^\d+[A-Za-z]?(?:\s+.*)?$"))
            {
                parts.RemoveAt(i);
                break;
            }
        }

        return string.Join(", ", parts);
    }

    private sealed class ClientMessagesConfigStorage
    {
        public string CloseConfirmationText { get; set; } = string.Empty;
        public string ClosingText { get; set; } = string.Empty;
    }
}
