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

        if (string.Equals(text, "Cancelar", StringComparison.OrdinalIgnoreCase))
        {
            await GoHomeAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(text, "Voltar", StringComparison.OrdinalIgnoreCase))
        {
            await GoHomeAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(text, "?? Trocar para Prestador", StringComparison.OrdinalIgnoreCase)
            && context.User.Role == UserRole.Both)
        {
            UserContextService.SetState(context.Session, BotStates.P_HOME);
            context.Session.ActiveJobId = null;
            context.Session.IsChatActive = false;
            await context.Db.SaveChangesAsync(cancellationToken);
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                BotMessages.ProviderHomeMenu(),
                KeyboardFactory.ProviderMenu(),
                null,
                cancellationToken);
            return;
        }

        if (string.Equals(text, "??? Pedir servico", StringComparison.OrdinalIgnoreCase))
        {
            await StartWizardAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(text, "?? Meus agendamentos", StringComparison.OrdinalIgnoreCase))
        {
            await SendMyJobsAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (string.Equals(text, "? Ajuda", StringComparison.OrdinalIgnoreCase))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Use o menu para pedir servico, acompanhar agendamentos e conversar com o prestador.",
                KeyboardFactory.ClientMenu(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        if (string.Equals(text, "? Favoritos", StringComparison.OrdinalIgnoreCase))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                "Favoritos ainda nao foi configurado para sua conta.",
                KeyboardFactory.ClientMenu(),
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
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    context.User.TelegramUserId,
                    message.Chat.Id,
                    BotMessages.ClientHomeMenu(),
                    KeyboardFactory.ClientMenu(),
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
                KeyboardFactory.ClientMenu(),
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
                KeyboardFactory.ClientMenu(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        context.Draft.Latitude = message.Location?.Latitude;
        context.Draft.Longitude = message.Location?.Longitude;
        context.Draft.AddressText = $"Localizacao enviada via Telegram (lat={context.Draft.Latitude};lng={context.Draft.Longitude})";

        UserContextService.SaveDraft(context.Session, context.Draft);
        UserContextService.SetState(context.Session, BotStates.C_SCHEDULE);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            message.Chat.Id,
            BotMessages.AskSchedule(),
            KeyboardFactory.ScheduleMode(),
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

        if (route.Scope == "C" && route.Action == "CAT")
        {
            if (!long.TryParse(route.Arg1, out var categoryId))
            {
                return true;
            }

            var category = await context.Db.ServiceCategories
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == categoryId && x.TenantId == context.TenantId,
                    cancellationToken);

            if (category is null)
            {
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
                KeyboardFactory.LocationRequestKeyboard(),
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
                    KeyboardFactory.ClientMenu(),
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
                KeyboardFactory.ClientMenu(),
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

        if (job is null || !job.ProviderUserId.HasValue || job.Status is JobStatus.Cancelled or JobStatus.Finished)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                chatId,
                "Chat indisponivel para esse pedido.",
                KeyboardFactory.ClientMenu(),
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

    public async Task SendMyJobsAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        var jobs = await context.Db.Jobs
            .AsNoTracking()
            .Where(x => x.ClientUserId == context.User.Id)
            .OrderByDescending(x => x.CreatedAt)
            .Take(10)
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
                KeyboardFactory.ClientMenu(),
                null,
                cancellationToken);

            return;
        }

        foreach (var job in jobs)
        {
            var status = job.Status.ToString();
            var when = job.IsUrgent
                ? "Urgente"
                : (job.ScheduledAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "Nao definido");

            var text = $"#{job.Id} | {job.Category}\nStatus: {status}\nQuando: {when}";
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("?? Chat", $"J:{job.Id}:CHAT")
                }
            });

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
    }

    public async Task StartWizardAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        var categories = await _jobWorkflow.GetCategoriesAsync(context.Db, context.TenantId, cancellationToken);

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
        context.Draft.Latitude = null;
        context.Draft.Longitude = null;
        context.Draft.IsUrgent = false;
        context.Draft.ScheduledAt = null;
        context.Draft.PreferenceCode = null;
        context.Draft.PhotoFileIds.Clear();

        UserContextService.SaveDraft(context.Session, context.Draft);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            BotMessages.AskCategory(),
            KeyboardFactory.Categories(categories),
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
        if (string.Equals(text, "Concluir fotos", StringComparison.OrdinalIgnoreCase))
        {
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
                KeyboardFactory.LocationRequestKeyboard(),
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
        if (!JobWorkflowService.HasCep(text))
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                context.User.TelegramUserId,
                message.Chat.Id,
                BotMessages.NeedAddressWithCep(),
                KeyboardFactory.LocationRequestKeyboard(),
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        context.Draft.AddressText = text;
        context.Draft.Cep = ExtractCep(text);
        UserContextService.SaveDraft(context.Session, context.Draft);
        UserContextService.SetState(context.Session, BotStates.C_SCHEDULE);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            message.Chat.Id,
            BotMessages.AskSchedule(),
            KeyboardFactory.ScheduleMode(),
            context.Session.ActiveJobId,
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

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            chatId,
            BotMessages.ClientHomeMenu(),
            KeyboardFactory.ClientMenu(),
            context.Session.ActiveJobId,
            cancellationToken);
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
}
