using System.Text.Json;
using BotAgendamentoAI.Telegram.Application.Callback;
using BotAgendamentoAI.Telegram.Application.Common;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using Microsoft.EntityFrameworkCore;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram.Features.Provider;

public sealed class ProviderFlowHandler
{
    private readonly TelegramMessageSender _sender;
    private readonly JobWorkflowService _jobWorkflow;

    public ProviderFlowHandler(TelegramMessageSender sender, JobWorkflowService jobWorkflow)
    {
        _sender = sender;
        _jobWorkflow = jobWorkflow;
    }

    public async Task HandleTextAsync(BotExecutionContext context, Message message, CancellationToken cancellationToken)
    {
        var text = (message.Text ?? string.Empty).Trim();
        var normalizedText = NormalizeProviderMenuInput(text, context.Session.State);

        if (text.Equals(MenuTexts.Cancel, StringComparison.OrdinalIgnoreCase)
            || text.Equals(MenuTexts.Back, StringComparison.OrdinalIgnoreCase))
        {
            await GoHomeAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (normalizedText.Equals(MenuTexts.ProviderAvailableJobs, StringComparison.OrdinalIgnoreCase))
        {
            await SendFeedAsync(context, message.Chat.Id, 0, cancellationToken);
            return;
        }

        if (normalizedText.Equals(MenuTexts.ProviderAgenda, StringComparison.OrdinalIgnoreCase))
        {
            await SendAgendaAsync(context, message.Chat.Id, 0, cancellationToken);
            return;
        }

        if (normalizedText.Equals(MenuTexts.ProviderProfile, StringComparison.OrdinalIgnoreCase))
        {
            await SendProfileAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (normalizedText.Equals(MenuTexts.ProviderPortfolio, StringComparison.OrdinalIgnoreCase))
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, message.Chat.Id,
                "Gerencie seu portfolio:", KeyboardFactory.PortfolioMenu(), null, cancellationToken);
            return;
        }

        if (normalizedText.Equals(MenuTexts.ProviderSettings, StringComparison.OrdinalIgnoreCase))
        {
            await ToggleAvailabilityAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (normalizedText.Equals(MenuTexts.ProviderSwitchToClient, StringComparison.OrdinalIgnoreCase)
            && context.User.Role == UserRole.Both)
        {
            context.Session.State = BotStates.C_HOME;
            context.Session.DraftJson = "{}";
            context.Session.ActiveJobId = null;
            context.Session.ChatJobId = null;
            context.Session.ChatPeerUserId = null;
            context.Session.IsChatActive = false;
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, message.Chat.Id,
                BotMessages.ClientHomeMenu(context.User.Role == UserRole.Both),
                KeyboardFactory.ClientHomeActions(context.User.Role == UserRole.Both),
                null,
                cancellationToken);
            return;
        }

        if (string.Equals(context.Session.State, BotStates.P_FINISH_JOB, StringComparison.Ordinal))
        {
            await HandleFinishTextAsync(context, message.Chat.Id, text, cancellationToken);
            return;
        }

        if (string.Equals(context.Session.State, BotStates.P_PROFILE_EDIT, StringComparison.Ordinal))
        {
            await HandleProfileEditTextAsync(context, message.Chat.Id, text, cancellationToken);
            return;
        }

        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, message.Chat.Id,
            BotMessages.ProviderHomeMenu(), KeyboardFactory.ProviderMenu(), context.Session.ActiveJobId, cancellationToken);
    }

    public async Task HandlePhotoAsync(BotExecutionContext context, Message message, CancellationToken cancellationToken)
    {
        if (message.Photo is null || message.Photo.Length == 0)
        {
            return;
        }

        var fileId = message.Photo[^1].FileId;
        if (string.Equals(context.Session.State, BotStates.P_PORTFOLIO_UPLOAD, StringComparison.Ordinal))
        {
            context.Db.ProviderPortfolioPhotos.Add(new ProviderPortfolioPhoto
            {
                ProviderUserId = context.User.Id,
                FileIdOrUrl = fileId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.Db.SaveChangesAsync(cancellationToken);
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, message.Chat.Id,
                "Foto adicionada ao portfolio.", KeyboardFactory.PortfolioMenu(), null, cancellationToken);
            return;
        }

        if (string.Equals(context.Session.State, BotStates.P_FINISH_JOB, StringComparison.Ordinal) && context.Session.ActiveJobId.HasValue)
        {
            context.Db.JobPhotos.Add(new JobPhoto
            {
                JobId = context.Session.ActiveJobId.Value,
                TelegramFileId = fileId,
                Kind = "after",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.Db.SaveChangesAsync(cancellationToken);
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, message.Chat.Id,
                "Foto do depois registrada.", KeyboardFactory.FinishWizardActions(context.Session.ActiveJobId.Value), context.Session.ActiveJobId, cancellationToken);
        }
    }

    public async Task<bool> HandleCallbackAsync(BotExecutionContext context, CallbackRoute route, CallbackQuery callback, CancellationToken cancellationToken)
    {
        var chatId = callback.Message?.Chat.Id ?? context.User.TelegramUserId;

        if (route.Scope == "NAV")
        {
            await GoHomeAsync(context, chatId, cancellationToken);
            return true;
        }

        if (route.Scope == "P" && route.Action == "PRF")
        {
            if (route.Arg1 == "BIO")
            {
                UserContextService.SetState(context.Session, BotStates.P_PROFILE_EDIT);
                context.Draft.PreferenceCode = "P:BIO";
                UserContextService.SaveDraft(context.Session, context.Draft);
                await context.Db.SaveChangesAsync(cancellationToken);

                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    "Envie a nova bio do seu perfil (max 500 caracteres).",
                    null,
                    null,
                    cancellationToken);
                return true;
            }

            if (route.Arg1 == "RAD")
            {
                UserContextService.SetState(context.Session, BotStates.P_PROFILE_EDIT);
                context.Draft.PreferenceCode = "P:RAD";
                UserContextService.SaveDraft(context.Session, context.Draft);
                await context.Db.SaveChangesAsync(cancellationToken);

                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    "Informe o novo raio de atendimento em km (1 a 200).",
                    null,
                    null,
                    cancellationToken);
                return true;
            }

            if (route.Arg1 == "LOC")
            {
                UserContextService.SetState(context.Session, BotStates.P_PROFILE_EDIT);
                context.Draft.PreferenceCode = "P:LOC";
                UserContextService.SaveDraft(context.Session, context.Draft);
                await context.Db.SaveChangesAsync(cancellationToken);

                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    "Envie sua localizacao para definir o ponto base do atendimento.",
                    KeyboardFactory.LocationRequestKeyboard(),
                    null,
                    cancellationToken);
                return true;
            }

            if (route.Arg1 == "CAT")
            {
                await StartCategoryEditAsync(context, chatId, cancellationToken);
                return true;
            }
        }

        if (route.Scope == "P" && route.Action == "CAT")
        {
            if (!long.TryParse(route.Arg1, out var categoryId))
            {
                return true;
            }

            await ToggleCategoryEditAsync(context, chatId, categoryId, cancellationToken);
            return true;
        }

        if (route.Scope == "P" && route.Action == "CATSAVE")
        {
            await SaveCategoryEditAsync(context, chatId, cancellationToken);
            return true;
        }

        if (route.Scope == "P" && route.Action == "POR")
        {
            if (route.Arg1 == "UP")
            {
                UserContextService.SetState(context.Session, BotStates.P_PORTFOLIO_UPLOAD);
                await context.Db.SaveChangesAsync(cancellationToken);
                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    BotMessages.PortfolioUploadHint(), KeyboardFactory.PortfolioMenu(), null, cancellationToken);
                return true;
            }

            if (route.Arg1 == "VW")
            {
                var offset = int.TryParse(route.Arg2, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
                await SendPortfolioGalleryAsync(context, chatId, offset, cancellationToken);
                return true;
            }

            if (route.Arg1 == "RM")
            {
                var offset = int.TryParse(route.Arg2, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
                await SendPortfolioRemoveMenuAsync(context, chatId, offset, cancellationToken);
                return true;
            }
        }

        if (route.Scope == "P" && route.Action == "PRD")
        {
            if (!long.TryParse(route.Arg1, out var photoId))
            {
                return true;
            }

            var offset = int.TryParse(route.Arg2, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
            var photo = await context.Db.ProviderPortfolioPhotos
                .FirstOrDefaultAsync(x => x.Id == photoId && x.ProviderUserId == context.User.Id, cancellationToken);

            if (photo is not null)
            {
                context.Db.ProviderPortfolioPhotos.Remove(photo);
                await context.Db.SaveChangesAsync(cancellationToken);
            }

            await SendPortfolioRemoveMenuAsync(context, chatId, offset, cancellationToken);
            return true;
        }

        if (route.Scope == "P" && route.Action == "FEED")
        {
            var offset = int.TryParse(route.Arg1, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
            await SendFeedAsync(context, chatId, offset, cancellationToken);
            return true;
        }

        if (route.Scope == "P" && route.Action == "AGD")
        {
            var offset = int.TryParse(route.Arg1, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
            await SendAgendaAsync(context, chatId, offset, cancellationToken);
            return true;
        }

        if (route.Scope != "J" || !long.TryParse(route.Action, out var jobId))
        {
            return false;
        }

        var job = await context.Db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId && x.TenantId == context.TenantId, cancellationToken);
        if (job is null)
        {
            return true;
        }

        if (route.Arg1 == "DET")
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                $"Pedido #{job.Id}\nCategoria: {job.Category}\n{job.Description}\nEndereco: {job.AddressText}",
                KeyboardFactory.JobCardActions(job.Id), job.Id, cancellationToken);
            await _jobWorkflow.SendJobGalleryAsync(context, job.Id, 0, chatId, context.User.TelegramUserId, cancellationToken);
            return true;
        }

        if (route.Arg1 == "GAL")
        {
            var offset = int.TryParse(route.Arg2, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
            await _jobWorkflow.SendJobGalleryAsync(context, job.Id, offset, chatId, context.User.TelegramUserId, cancellationToken);
            return true;
        }

        if (route.Arg1 == "ACC")
        {
            if (job.Status != JobStatus.WaitingProvider)
            {
                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    "Esse pedido ja foi aceito.", KeyboardFactory.ProviderMenu(), job.Id, cancellationToken);
                return true;
            }

            job.ProviderUserId = context.User.Id;
            job.Status = JobStatus.Accepted;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            context.Session.ActiveJobId = job.Id;
            context.Session.State = BotStates.P_ACTIVE_JOB;
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                $"Voce aceitou o pedido #{job.Id}.", KeyboardFactory.ProviderTimeline(job.Id), job.Id, cancellationToken);

            await NotifyClientAsync(context, job, $"Seu pedido #{job.Id} foi aceito por {context.User.Name}.", cancellationToken);
            return true;
        }

        if (route.Arg1 == "REJ")
        {
            if (!context.Draft.HiddenFeedJobIds.Contains(job.Id))
            {
                context.Draft.HiddenFeedJobIds.Add(job.Id);
                UserContextService.SaveDraft(context.Session, context.Draft);
                await context.Db.SaveChangesAsync(cancellationToken);
            }

            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                $"Pedido #{job.Id} ocultado do seu feed.", null, job.Id, cancellationToken);
            return true;
        }

        if (route.Arg1 == "CHAT")
        {
            if (route.Arg2 == "EXIT")
            {
                context.Session.IsChatActive = false;
                context.Session.ChatPeerUserId = null;
                context.Session.ChatJobId = null;
                context.Session.State = BotStates.P_ACTIVE_JOB;
                context.Session.UpdatedAt = DateTimeOffset.UtcNow;
                await context.Db.SaveChangesAsync(cancellationToken);
                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    BotMessages.ChatClosed(), KeyboardFactory.ProviderTimeline(job.Id), job.Id, cancellationToken);
                return true;
            }

            await OpenChatAsync(context, job.Id, chatId, cancellationToken);
            return true;
        }

        if (route.Arg1 == "S")
        {
            return await HandleTimelineAsync(context, job, route.Arg2, chatId, cancellationToken);
        }

        return true;
    }

    public async Task OpenChatAsync(BotExecutionContext context, long jobId, ChatId chatId, CancellationToken cancellationToken)
    {
        var job = await context.Db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId && x.ProviderUserId == context.User.Id, cancellationToken);
        if (job is null || !CanOpenChat(job.Status))
        {
            return;
        }

        context.Session.ActiveJobId = jobId;
        context.Session.ChatJobId = jobId;
        context.Session.ChatPeerUserId = job.ClientUserId;
        context.Session.IsChatActive = true;
        context.Session.State = BotStates.CHAT_MEDIATED;
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            BotMessages.ChatOpened(), KeyboardFactory.ChatActions(job.Id), job.Id, cancellationToken);
    }

    public async Task HandleProviderLocationAsync(BotExecutionContext context, Message message, CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Session.State, BotStates.P_PROFILE_EDIT, StringComparison.Ordinal)
            || !string.Equals(context.Draft.PreferenceCode, "P:LOC", StringComparison.Ordinal))
        {
            return;
        }

        var profile = await EnsureProfileAsync(context, cancellationToken);
        profile.BaseLatitude = message.Location?.Latitude;
        profile.BaseLongitude = message.Location?.Longitude;
        context.Session.State = BotStates.P_HOME;
        context.Draft.PreferenceCode = null;
        UserContextService.SaveDraft(context.Session, context.Draft);
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, message.Chat.Id,
            "Local base atualizada com sucesso.",
            KeyboardFactory.ProviderMenu(),
            null,
            cancellationToken);
    }

    private async Task<bool> HandleTimelineAsync(BotExecutionContext context, Job job, string step, ChatId chatId, CancellationToken cancellationToken)
    {
        if (step == "OTW" || step == "ARR" || step == "STA")
        {
            job.Status = step switch
            {
                "OTW" => JobStatus.OnTheWay,
                "ARR" => JobStatus.Arrived,
                _ => JobStatus.InProgress
            };
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                $"Status atualizado: {job.Status}", KeyboardFactory.ProviderTimeline(job.Id), job.Id, cancellationToken);
            await NotifyClientAsync(context, job, $"Atualizacao do pedido #{job.Id}: {job.Status}", cancellationToken);
            return true;
        }

        if (step == "FIN")
        {
            context.Session.ActiveJobId = job.Id;
            context.Session.State = BotStates.P_FINISH_JOB;
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
            context.Draft.FinalAmount = null;
            context.Draft.FinalNotes = null;
            UserContextService.SaveDraft(context.Session, context.Draft);
            await context.Db.SaveChangesAsync(cancellationToken);
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Envie valor | observacoes e depois clique em Concluir finalizacao.",
                KeyboardFactory.FinishWizardActions(job.Id),
                job.Id,
                cancellationToken);
            return true;
        }

        if (step == "DONE")
        {
            if (!context.Draft.FinalAmount.HasValue)
            {
                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    "Antes de concluir, envie: valor | observacoes.",
                    KeyboardFactory.FinishWizardActions(job.Id),
                    job.Id,
                    cancellationToken);
                return true;
            }

            job.FinalAmount = context.Draft.FinalAmount;
            job.FinalNotes = context.Draft.FinalNotes;
            job.Status = JobStatus.Finished;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            context.Session.State = BotStates.P_HOME;
            context.Session.ActiveJobId = null;
            context.Session.DraftJson = "{}";
            context.Session.UpdatedAt = DateTimeOffset.UtcNow;
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                $"Pedido #{job.Id} finalizado.", KeyboardFactory.ProviderMenu(), job.Id, cancellationToken);

            await NotifyClientAsync(context, job, $"Seu pedido #{job.Id} foi finalizado. Avalie:", cancellationToken, KeyboardFactory.Rating(job.Id));
            return true;
        }

        return true;
    }

    private async Task HandleFinishTextAsync(BotExecutionContext context, ChatId chatId, string text, CancellationToken cancellationToken)
    {
        var parts = text.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !decimal.TryParse(parts[0].Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                BotMessages.InvalidFinishFormat(),
                context.Session.ActiveJobId.HasValue ? KeyboardFactory.FinishWizardActions(context.Session.ActiveJobId.Value) : null,
                context.Session.ActiveJobId,
                cancellationToken);
            return;
        }

        context.Draft.FinalAmount = Math.Round(value, 2);
        context.Draft.FinalNotes = parts.Length > 1 ? parts[1] : string.Empty;
        UserContextService.SaveDraft(context.Session, context.Draft);
        await context.Db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleProfileEditTextAsync(BotExecutionContext context, ChatId chatId, string text, CancellationToken cancellationToken)
    {
        var mode = context.Draft.PreferenceCode ?? string.Empty;
        var profile = await EnsureProfileAsync(context, cancellationToken);

        if (mode == "P:BIO")
        {
            var bio = (text ?? string.Empty).Trim();
            if (bio.Length is < 3 or > 500)
            {
                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    "Bio invalida. Envie entre 3 e 500 caracteres.",
                    null,
                    null,
                    cancellationToken);
                return;
            }

            profile.Bio = bio;
            context.Draft.PreferenceCode = null;
            UserContextService.SaveDraft(context.Session, context.Draft);
            UserContextService.SetState(context.Session, BotStates.P_HOME);
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Bio atualizada com sucesso.",
                KeyboardFactory.ProviderProfileActions(),
                null,
                cancellationToken);
            return;
        }

        if (mode == "P:RAD")
        {
            if (!int.TryParse(text, out var radius) || radius is < 1 or > 200)
            {
                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                    "Raio invalido. Informe um numero entre 1 e 200.",
                    null,
                    null,
                    cancellationToken);
                return;
            }

            profile.RadiusKm = radius;
            context.Draft.PreferenceCode = null;
            UserContextService.SaveDraft(context.Session, context.Draft);
            UserContextService.SetState(context.Session, BotStates.P_HOME);
            await context.Db.SaveChangesAsync(cancellationToken);

            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                $"Raio atualizado para {radius} km.",
                KeyboardFactory.ProviderProfileActions(),
                null,
                cancellationToken);
            return;
        }

        if (mode == "P:CAT")
        {
            var categories = await _jobWorkflow.GetCategoriesAsync(context.Db, context.TenantId, cancellationToken);
            var selected = new HashSet<string>(context.Draft.ProviderCategoryNames, StringComparer.OrdinalIgnoreCase);
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Use os botoes para marcar/desmarcar categorias e depois toque em 'Salvar categorias'.",
                KeyboardFactory.ProviderCategorySelection(categories, selected),
                null,
                cancellationToken);
            return;
        }

        if (mode == "P:LOC")
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Para definir local base, envie uma localizacao pelo Telegram.",
                KeyboardFactory.LocationRequestKeyboard(),
                null,
                cancellationToken);
            return;
        }

        await GoHomeAsync(context, chatId, cancellationToken);
    }

    private async Task StartCategoryEditAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        var profile = await EnsureProfileAsync(context, cancellationToken);
        var categories = await _jobWorkflow.GetCategoriesAsync(context.Db, context.TenantId, cancellationToken);

        context.Draft.PreferenceCode = "P:CAT";
        context.Draft.ProviderCategoryNames = ParseCategories(profile.CategoriesJson);
        UserContextService.SetState(context.Session, BotStates.P_PROFILE_EDIT);
        UserContextService.SaveDraft(context.Session, context.Draft);
        await context.Db.SaveChangesAsync(cancellationToken);

        var selected = new HashSet<string>(context.Draft.ProviderCategoryNames, StringComparer.OrdinalIgnoreCase);
        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            "Selecione as categorias atendidas:",
            KeyboardFactory.ProviderCategorySelection(categories, selected),
            null,
            cancellationToken);
    }

    private async Task ToggleCategoryEditAsync(BotExecutionContext context, ChatId chatId, long categoryId, CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Draft.PreferenceCode, "P:CAT", StringComparison.Ordinal))
        {
            await StartCategoryEditAsync(context, chatId, cancellationToken);
            return;
        }

        var category = await context.Db.ServiceCategories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == categoryId && x.TenantId == context.TenantId, cancellationToken);

        if (category is null)
        {
            return;
        }

        var selected = context.Draft.ProviderCategoryNames;
        var existing = selected.FirstOrDefault(x => string.Equals(x, category.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            selected.Add(category.Name);
        }
        else
        {
            selected.Remove(existing);
        }

        context.Draft.ProviderCategoryNames = selected
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UserContextService.SaveDraft(context.Session, context.Draft);
        await context.Db.SaveChangesAsync(cancellationToken);

        var categories = await _jobWorkflow.GetCategoriesAsync(context.Db, context.TenantId, cancellationToken);
        var selectedSet = new HashSet<string>(context.Draft.ProviderCategoryNames, StringComparer.OrdinalIgnoreCase);
        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            "Categorias atualizadas no rascunho.",
            KeyboardFactory.ProviderCategorySelection(categories, selectedSet),
            null,
            cancellationToken);
    }

    private async Task SaveCategoryEditAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        var profile = await EnsureProfileAsync(context, cancellationToken);
        profile.CategoriesJson = SerializeCategories(context.Draft.ProviderCategoryNames);
        context.Draft.PreferenceCode = null;
        UserContextService.SaveDraft(context.Session, context.Draft);
        UserContextService.SetState(context.Session, BotStates.P_HOME);
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            "Categorias salvas com sucesso.",
            KeyboardFactory.ProviderProfileActions(),
            null,
            cancellationToken);
    }

    private async Task SendFeedAsync(BotExecutionContext context, ChatId chatId, int offset, CancellationToken cancellationToken)
    {
        const int pageSize = 5;
        var safeOffset = Math.Max(0, offset);

        var profile = await EnsureProfileAsync(context, cancellationToken);
        var query = context.Db.Jobs
            .AsNoTracking()
            .Where(x => x.TenantId == context.TenantId && x.Status == JobStatus.WaitingProvider)
            .OrderByDescending(x => x.CreatedAt);

        var total = await query.CountAsync(cancellationToken);
        var jobs = await query.Skip(safeOffset).Take(pageSize).ToListAsync(cancellationToken);
        jobs = jobs
            .Where(x => !context.Draft.HiddenFeedJobIds.Contains(x.Id))
            .Where(x => profile.CategoriesJson == "[]" || profile.CategoriesJson.Contains(x.Category, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (jobs.Count == 0)
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                BotMessages.NoProviderJobs(), KeyboardFactory.ProviderMenu(), null, cancellationToken);
            return;
        }

        foreach (var job in jobs)
        {
            var photo = await context.Db.JobPhotos
                .AsNoTracking()
                .Where(x => x.JobId == job.Id)
                .OrderBy(x => x.Id)
                .Select(x => x.TelegramFileId)
                .FirstOrDefaultAsync(cancellationToken);

            var caption = $"Pedido #{job.Id}\nCategoria: {job.Category}\n{job.Description}\nEndereco: {job.AddressText}";
            if (!string.IsNullOrWhiteSpace(photo))
            {
                await _sender.SendPhotoCardAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId, photo, caption,
                    KeyboardFactory.JobCardActions(job.Id), job.Id, cancellationToken);
            }
            else
            {
                await _sender.SendTextAsync(
                    context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId, caption,
                    KeyboardFactory.JobCardActions(job.Id), job.Id, cancellationToken);
            }
        }

        var navButtons = new List<InlineKeyboardButton>();
        if (safeOffset > 0)
        {
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Anterior", $"P:FEED:{Math.Max(0, safeOffset - pageSize)}"));
        }

        if (safeOffset + pageSize < total)
        {
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Proximos", $"P:FEED:{safeOffset + pageSize}"));
        }

        if (navButtons.Count > 0)
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Navegacao do feed:", new InlineKeyboardMarkup(new[] { navButtons.ToArray() }), null, cancellationToken);
        }
    }

    private async Task SendAgendaAsync(BotExecutionContext context, ChatId chatId, int offset, CancellationToken cancellationToken)
    {
        const int pageSize = 5;
        var safeOffset = Math.Max(0, offset);

        var query = context.Db.Jobs
            .AsNoTracking()
            .Where(x => x.ProviderUserId == context.User.Id && x.Status != JobStatus.Cancelled && x.Status != JobStatus.Finished)
            .OrderByDescending(x => x.UpdatedAt);

        var total = await query.CountAsync(cancellationToken);
        var jobs = await query.Skip(safeOffset).Take(pageSize).ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Sua agenda esta vazia.", KeyboardFactory.ProviderMenu(), null, cancellationToken);
            return;
        }

        foreach (var job in jobs)
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                $"#{job.Id} | {job.Category}\nStatus: {job.Status}",
                KeyboardFactory.ProviderTimeline(job.Id),
                job.Id,
                cancellationToken);
        }

        var navButtons = new List<InlineKeyboardButton>();
        if (safeOffset > 0)
        {
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Anterior", $"P:AGD:{Math.Max(0, safeOffset - pageSize)}"));
        }

        if (safeOffset + pageSize < total)
        {
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Proximos", $"P:AGD:{safeOffset + pageSize}"));
        }

        if (navButtons.Count > 0)
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Navegacao da agenda:", new InlineKeyboardMarkup(new[] { navButtons.ToArray() }), null, cancellationToken);
        }
    }

    private async Task SendProfileAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        var profile = await EnsureProfileAsync(context, cancellationToken);
        var categories = ParseCategories(profile.CategoriesJson);
        var categoriesText = categories.Count == 0 ? "Todas" : string.Join(", ", categories);

        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            $"Perfil Prestador\nBio: {profile.Bio}\nCategorias: {categoriesText}\nRaio: {profile.RadiusKm} km\nDisponivel: {(profile.IsAvailable ? "Sim" : "Nao")}",
            KeyboardFactory.ProviderProfileActions(), null, cancellationToken);
    }

    private async Task ToggleAvailabilityAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        var profile = await EnsureProfileAsync(context, cancellationToken);
        profile.IsAvailable = !profile.IsAvailable;
        await context.Db.SaveChangesAsync(cancellationToken);
        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            $"Disponibilidade: {(profile.IsAvailable ? "ATIVO" : "PAUSADO")}",
            KeyboardFactory.ProviderMenu(),
            context.Session.ActiveJobId,
            cancellationToken);
    }

    private async Task SendPortfolioGalleryAsync(BotExecutionContext context, ChatId chatId, int offset, CancellationToken cancellationToken)
    {
        const int pageSize = 10;
        var safeOffset = Math.Max(0, offset);

        var query = context.Db.ProviderPortfolioPhotos.AsNoTracking()
            .Where(x => x.ProviderUserId == context.User.Id)
            .OrderByDescending(x => x.CreatedAt);

        var total = await query.CountAsync(cancellationToken);
        var photos = await query
            .Skip(safeOffset)
            .Take(pageSize)
            .Select(x => x.FileIdOrUrl)
            .ToListAsync(cancellationToken);

        if (photos.Count == 0)
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Portfolio vazio.", KeyboardFactory.PortfolioMenu(), null, cancellationToken);
            return;
        }

        await _sender.SendMediaGroupAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            photos, "Seu portfolio", null, cancellationToken);

        if (safeOffset + photos.Count < total)
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Existem mais fotos no seu portfolio.",
                KeyboardFactory.GalleryNext("P:POR:VW", safeOffset + photos.Count),
                null,
                cancellationToken);
        }
    }

    private async Task SendPortfolioRemoveMenuAsync(BotExecutionContext context, ChatId chatId, int offset, CancellationToken cancellationToken)
    {
        const int pageSize = 5;
        var safeOffset = Math.Max(0, offset);

        var query = context.Db.ProviderPortfolioPhotos.AsNoTracking()
            .Where(x => x.ProviderUserId == context.User.Id)
            .OrderByDescending(x => x.CreatedAt);

        var total = await query.CountAsync(cancellationToken);
        var photos = await query.Skip(safeOffset).Take(pageSize).ToListAsync(cancellationToken);

        if (photos.Count == 0)
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
                "Nao ha fotos para remover.", KeyboardFactory.PortfolioMenu(), null, cancellationToken);
            return;
        }

        var rows = photos
            .Select(x => new[]
            {
                InlineKeyboardButton.WithCallbackData($"Remover #{x.Id}", $"P:PRD:{x.Id}:{safeOffset}")
            })
            .ToList();

        var nav = new List<InlineKeyboardButton>();
        if (safeOffset > 0)
        {
            nav.Add(InlineKeyboardButton.WithCallbackData("Anterior", $"P:POR:RM:{Math.Max(0, safeOffset - pageSize)}"));
        }

        if (safeOffset + photos.Count < total)
        {
            nav.Add(InlineKeyboardButton.WithCallbackData("Proximos", $"P:POR:RM:{safeOffset + photos.Count}"));
        }

        if (nav.Count > 0)
        {
            rows.Add(nav.ToArray());
        }

        rows.Add(KeyboardFactory.NavigationRow());

        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            "Selecione uma foto para remover:",
            new InlineKeyboardMarkup(rows),
            null,
            cancellationToken);
    }

    private async Task<ProviderProfile> EnsureProfileAsync(BotExecutionContext context, CancellationToken cancellationToken)
    {
        var profile = await context.Db.ProvidersProfile.FirstOrDefaultAsync(x => x.UserId == context.User.Id, cancellationToken);
        if (profile is not null)
        {
            return profile;
        }

        profile = new ProviderProfile
        {
            UserId = context.User.Id,
            Bio = string.Empty,
            CategoriesJson = "[]",
            RadiusKm = 10,
            AvgRating = 0,
            TotalReviews = 0,
            IsAvailable = true
        };
        context.Db.ProvidersProfile.Add(profile);
        await context.Db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    private async Task NotifyClientAsync(
        BotExecutionContext context,
        Job job,
        string text,
        CancellationToken cancellationToken,
        InlineKeyboardMarkup? keyboard = null)
    {
        var client = await context.Db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == job.ClientUserId, cancellationToken);
        if (client is null)
        {
            return;
        }

        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, client.TelegramUserId, client.TelegramUserId,
            text, keyboard, job.Id, cancellationToken);
    }

    private async Task GoHomeAsync(BotExecutionContext context, ChatId chatId, CancellationToken cancellationToken)
    {
        context.Session.State = BotStates.P_HOME;
        context.Session.DraftJson = "{}";
        context.Session.IsChatActive = false;
        context.Session.ChatPeerUserId = null;
        context.Session.ChatJobId = null;
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            BotMessages.ProviderHomeMenu(), KeyboardFactory.ProviderMenu(), context.Session.ActiveJobId, cancellationToken);
    }

    private static List<string> ParseCategories(string? categoriesJson)
    {
        if (string.IsNullOrWhiteSpace(categoriesJson))
        {
            return new List<string>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(categoriesJson);
            return parsed?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        catch
        {
            return categoriesJson
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static string SerializeCategories(IEnumerable<string> categoryNames)
    {
        var normalized = categoryNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized);
    }

    private static bool CanOpenChat(JobStatus status)
        => status is JobStatus.Accepted or JobStatus.OnTheWay or JobStatus.Arrived or JobStatus.InProgress;

    private static string NormalizeProviderMenuInput(string text, string state)
    {
        if (!IsProviderMenuState(state))
        {
            return text;
        }

        return text switch
        {
            "1" => MenuTexts.ProviderAvailableJobs,
            "2" => MenuTexts.ProviderAgenda,
            "3" => MenuTexts.ProviderProfile,
            "4" => MenuTexts.ProviderPortfolio,
            "5" => MenuTexts.ProviderSettings,
            "6" => MenuTexts.ProviderSwitchToClient,
            _ => text
        };
    }

    private static bool IsProviderMenuState(string state)
        => string.Equals(state, BotStates.P_HOME, StringComparison.Ordinal)
           || string.Equals(state, BotStates.P_ACTIVE_JOB, StringComparison.Ordinal)
           || string.Equals(state, BotStates.P_FEED, StringComparison.Ordinal)
           || string.Equals(state, BotStates.NONE, StringComparison.Ordinal);
}
