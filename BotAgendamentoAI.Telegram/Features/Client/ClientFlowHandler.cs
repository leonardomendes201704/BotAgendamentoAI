using System.Net.Http;
using System.Text.Json;
using BotAgendamentoAI.Telegram.Application.Callback;
using BotAgendamentoAI.Telegram.Application.Common;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using Microsoft.EntityFrameworkCore;
using BotAgendamentoAI.Telegram.TelegramCompat;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram.Features.Client;

public sealed class ClientFlowHandler
{
    private static readonly HttpClient ViaCepHttpClient = BuildViaCepHttpClient();
    private static readonly HttpClient GeocodeHttpClient = BuildGeocodeHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TelegramMessageSender _sender;
    private readonly JobWorkflowService _jobWorkflow;
    private readonly IPhotoValidator _photoValidator;

    public ClientFlowHandler(
        TelegramMessageSender sender,
        JobWorkflowService jobWorkflow,
        IPhotoValidator photoValidator)
    {
        _sender = sender;
        _jobWorkflow = jobWorkflow;
        _photoValidator = photoValidator;
    }

    public async Task HandleTextAsync(BotExecutionContext context, Message message, CancellationToken cancellationToken)
    {
        var text = (message.Text ?? string.Empty).Trim();
        var state = context.Session.State;
        var normalizedText = NormalizeClientMenuInput(text, state);

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

            case BotStates.C_RATING:
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    message.Chat.Id,
                    "Toque nas estrelas para avaliar o atendimento.",
                    null,
                    context.Session.ActiveJobId,
                    cancellationToken);
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

        if (route.Scope == "C" && route.Action == "HOME")
        {
            await HandleHomeCallbackAsync(context, chatId, route.Arg1, cancellationToken);
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
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, context.Runtime.TimeZone);
                context.Draft.IsUrgent = false;
                context.Draft.ScheduledAt = nowLocal.Date.AddHours(Math.Max(nowLocal.Hour + 1, 9));

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
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    chatId,
                    "Selecione o dia:",
                    KeyboardFactory.DaySelection(nowLocal),
                    context.Session.ActiveJobId,
                    cancellationToken);

                return true;
            }
        }

        if (route.Scope == "C" && route.Action == "DAY")
        {
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
            UserContextService.SaveDraft(context.Session, context.Draft);
            UserContextService.SetState(context.Session, BotStates.C_CONFIRM);
            await context.Db.SaveChangesAsync(cancellationToken);

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

                var job = await _jobWorkflow.ConfirmDraftAsync(context, chatId, cancellationToken);
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
            if (!long.TryParse(route.Action, out var jobId) || !int.TryParse(route.Arg1, out var stars))
            {
                return true;
            }

            stars = Math.Clamp(stars, 1, 5);
            var job = await context.Db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId && x.ClientUserId == context.User.Id, cancellationToken);
            if (job is null || !job.ProviderUserId.HasValue)
            {
                return true;
            }

            var already = await context.Db.Ratings.AnyAsync(x => x.JobId == jobId, cancellationToken);
            if (!already)
            {
                context.Db.Ratings.Add(new Rating
                {
                    JobId = jobId,
                    ClientUserId = context.User.Id,
                    ProviderUserId = job.ProviderUserId.Value,
                    Stars = stars,
                    CreatedAt = DateTimeOffset.UtcNow
                });

                await context.Db.SaveChangesAsync(cancellationToken);
            }

            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Obrigado pela avaliacao!",
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                jobId,
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
            KeyboardFactory.ChatActions(job.Id),
            job.Id,
            cancellationToken);
    }

    public async Task SendMyJobsAsync(BotExecutionContext context, ChatId chatId, int offset, CancellationToken cancellationToken)
    {
        var safeOffset = Math.Max(0, offset);
        const int pageSize = 5;

        var total = await context.Db.Jobs
            .AsNoTracking()
            .CountAsync(x => x.ClientUserId == context.User.Id, cancellationToken);

        var jobs = await context.Db.Jobs
            .AsNoTracking()
            .Where(x => x.ClientUserId == context.User.Id)
            .OrderByDescending(x => x.CreatedAt)
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
            var status = job.Status.ToString();
            var when = job.IsUrgent
                ? "Urgente"
                : (job.ScheduledAt.HasValue
                    ? TimeZoneInfo.ConvertTime(job.ScheduledAt.Value, context.Runtime.TimeZone).ToString("dd/MM/yyyy HH:mm")
                    : "Nao definido");

            var text = $"#{job.Id} | {job.Category}\nStatus: {status}\nQuando: {when}";
            InlineKeyboardMarkup? keyboard = null;
            if (CanOpenChat(job.Status, job.ProviderUserId.HasValue))
            {
                keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Chat", $"J:{job.Id}:CHAT")
                    }
                });
            }

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

    public async Task StartWizardAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
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
                try
                {
                    await SendMyJobsAsync(context, chatId, 0, cancellationToken);
                }
                catch
                {
                    await SendClientHomeMenuAsync(
                        context,
                        chatId,
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
            BotMessages.ProviderHomeMenu(),
            KeyboardFactory.ProviderMenu(),
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
            var byAddress = await TryGeocodeByAddressAsync(baseAddress, cancellationToken);
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

        return TryGeocodeQueryAsync($"{safe}, Brasil", cancellationToken);
    }

    private static async Task<GeocodeResult> TryGeocodeQueryAsync(string query, CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(query);
        var endpoint = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=br&q={encoded}";

        try
        {
            using var response = await GeocodeHttpClient.GetAsync(endpoint, cancellationToken);
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

    private static string NormalizeClientMenuInput(string text, string state)
    {
        if (!IsClientMenuState(state))
        {
            return text;
        }

        return text switch
        {
            "1" => MenuTexts.ClientRequestService,
            "2" => MenuTexts.ClientMyBookings,
            "3" => MenuTexts.ClientFavorites,
            "4" => MenuTexts.ClientHelp,
            "5" => MenuTexts.ClientSwitchToProvider,
            _ => text
        };
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
}
