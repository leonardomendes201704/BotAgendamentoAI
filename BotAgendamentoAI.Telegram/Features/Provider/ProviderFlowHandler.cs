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

        if (text.Equals(MenuTexts.Cancel, StringComparison.OrdinalIgnoreCase)
            || text.Equals(MenuTexts.Back, StringComparison.OrdinalIgnoreCase))
        {
            await GoHomeAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (text.Equals(MenuTexts.ProviderAvailableJobs, StringComparison.OrdinalIgnoreCase))
        {
            await SendFeedAsync(context, message.Chat.Id, 0, cancellationToken);
            return;
        }

        if (text.Equals(MenuTexts.ProviderAgenda, StringComparison.OrdinalIgnoreCase))
        {
            await SendAgendaAsync(context, message.Chat.Id, 0, cancellationToken);
            return;
        }

        if (text.Equals(MenuTexts.ProviderProfile, StringComparison.OrdinalIgnoreCase))
        {
            await SendProfileAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (text.Equals(MenuTexts.ProviderPortfolio, StringComparison.OrdinalIgnoreCase))
        {
            await _sender.SendTextAsync(
                context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, message.Chat.Id,
                "Gerencie seu portfolio:", KeyboardFactory.PortfolioMenu(), null, cancellationToken);
            return;
        }

        if (text.Equals(MenuTexts.ProviderSettings, StringComparison.OrdinalIgnoreCase))
        {
            await ToggleAvailabilityAsync(context, message.Chat.Id, cancellationToken);
            return;
        }

        if (text.Equals(MenuTexts.ProviderSwitchToClient, StringComparison.OrdinalIgnoreCase)
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
                BotMessages.ClientHomeMenu(), KeyboardFactory.ClientMenu(), null, cancellationToken);
            return;
        }

        if (string.Equals(context.Session.State, BotStates.P_FINISH_JOB, StringComparison.Ordinal))
        {
            await HandleFinishTextAsync(context, message.Chat.Id, text, cancellationToken);
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
        context.Session.DraftJson = "{}";
        context.Session.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);
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
        await _sender.SendTextAsync(
            context.Db, context.Bot, context.TenantId, context.User.TelegramUserId, chatId,
            $"Perfil Prestador\nBio: {profile.Bio}\nCategorias: {profile.CategoriesJson}\nRaio: {profile.RadiusKm} km\nDisponivel: {(profile.IsAvailable ? "Sim" : "Nao")}",
            KeyboardFactory.ProviderMenu(), null, cancellationToken);
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

    private static bool CanOpenChat(JobStatus status)
        => status is JobStatus.Accepted or JobStatus.OnTheWay or JobStatus.Arrived or JobStatus.InProgress;
}
