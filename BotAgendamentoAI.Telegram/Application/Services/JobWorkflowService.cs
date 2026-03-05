using System.Text.RegularExpressions;
using BotAgendamentoAI.Telegram.Application.Common;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class JobWorkflowService
{
    private static readonly Regex CepRegex = new(@"\b\d{5}-?\d{3}\b", RegexOptions.Compiled);
    private readonly TelegramMessageSender _sender;

    public JobWorkflowService(TelegramMessageSender sender)
    {
        _sender = sender;
    }

    public async Task<IReadOnlyList<ServiceCategoryEntity>> GetCategoriesAsync(BotDbContext db, string tenantId, CancellationToken cancellationToken)
    {
        var safeTenant = NormalizeTenant(tenantId);
        var categories = await db.ServiceCategories
            .AsNoTracking()
            .Where(x => x.TenantId == safeTenant)
            .OrderBy(x => x.Name)
            .Take(50)
            .ToListAsync(cancellationToken);

        if (categories.Count > 0)
        {
            return categories;
        }

        var defaults = new[]
        {
            "Alvenaria",
            "Hidraulica",
            "Marcenaria",
            "Montagem de Moveis",
            "Serralheria",
            "Eletronicos",
            "Eletrodomesticos",
            "Ar-Condicionado"
        };

        var now = DateTimeOffset.UtcNow;
        var created = defaults.Select(name => new ServiceCategoryEntity
        {
            TenantId = safeTenant,
            Name = name,
            NormalizedName = NormalizeName(name),
            CreatedAtUtc = now
        }).ToList();

        db.ServiceCategories.AddRange(created);
        await db.SaveChangesAsync(cancellationToken);
        return created;
    }

    public static bool HasCep(string? addressText)
    {
        if (string.IsNullOrWhiteSpace(addressText))
        {
            return false;
        }

        return CepRegex.IsMatch(addressText);
    }

    public static string BuildConfirmationSummary(UserDraft draft)
    {
        var lines = new List<string>
        {
            $"Categoria: {draft.Category}",
            $"Descricao: {draft.Description}",
            $"Fotos: {draft.PhotoFileIds.Count}",
            $"Endereco: {draft.AddressText}",
            $"Quando: {(draft.IsUrgent ? "Urgente" : draft.ScheduledAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "Hoje")}",
            $"Preferencia: {PreferenceLabel(draft.PreferenceCode)}"
        };

        return string.Join("\n", lines.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    public async Task<Job> ConfirmDraftAsync(
        BotExecutionContext context,
        ChatId clientChatId,
        CancellationToken cancellationToken)
    {
        var draft = context.Draft;
        var now = DateTimeOffset.UtcNow;

        var job = new Job
        {
            TenantId = context.TenantId,
            ClientUserId = context.User.Id,
            ProviderUserId = null,
            Category = draft.Category ?? string.Empty,
            Description = draft.Description ?? string.Empty,
            Status = JobStatus.WaitingProvider,
            ScheduledAt = draft.ScheduledAt,
            IsUrgent = draft.IsUrgent,
            AddressText = draft.AddressText,
            Latitude = draft.Latitude,
            Longitude = draft.Longitude,
            PreferenceCode = draft.PreferenceCode ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        };

        context.Db.Jobs.Add(job);
        await context.Db.SaveChangesAsync(cancellationToken);

        foreach (var fileId in draft.PhotoFileIds.Distinct(StringComparer.Ordinal))
        {
            context.Db.JobPhotos.Add(new JobPhoto
            {
                JobId = job.Id,
                TelegramFileId = fileId,
                Kind = "before",
                CreatedAt = now
            });
        }

        await context.Db.SaveChangesAsync(cancellationToken);

        await _sender.SendTextAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            context.User.TelegramUserId,
            clientChatId,
            BotMessages.WaitingProvider(),
            KeyboardFactory.ClientMenu(),
            job.Id,
            cancellationToken);

        await PublishJobToProvidersAsync(context, job, cancellationToken);
        return job;
    }

    public async Task PublishJobToProvidersAsync(BotExecutionContext context, Job job, CancellationToken cancellationToken)
    {
        var profiles = await context.Db.ProvidersProfile
            .AsNoTracking()
            .Join(
                context.Db.Users.AsNoTracking(),
                profile => profile.UserId,
                user => user.Id,
                (profile, user) => new { profile, user })
            .Where(x => x.user.TenantId == context.TenantId && x.user.IsActive && x.profile.IsAvailable)
            .ToListAsync(cancellationToken);

        if (profiles.Count == 0)
        {
            return;
        }

        var filtered = profiles.Where(x => MatchesCategory(job.Category, x.profile.CategoriesJson));

        foreach (var target in filtered)
        {
            if (!PassesRadiusFilter(job, target.profile))
            {
                continue;
            }

            var caption = $"Novo pedido #{job.Id}\nCategoria: {job.Category}\n{job.Description}\nEndereco: {job.AddressText}";
            var firstPhoto = await context.Db.JobPhotos
                .AsNoTracking()
                .Where(x => x.JobId == job.Id)
                .OrderBy(x => x.Id)
                .Select(x => x.TelegramFileId)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(firstPhoto))
            {
                await _sender.SendPhotoCardAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    target.user.TelegramUserId,
                    target.user.TelegramUserId,
                    firstPhoto,
                    caption,
                    KeyboardFactory.JobCardActions(job.Id),
                    job.Id,
                    cancellationToken);
            }
            else
            {
                await _sender.SendTextAsync(
                    context.Db,
                    context.Bot,
                    context.TenantId,
                    target.user.TelegramUserId,
                    target.user.TelegramUserId,
                    caption,
                    KeyboardFactory.JobCardActions(job.Id),
                    job.Id,
                    cancellationToken);
            }
        }
    }

    public async Task SendJobGalleryAsync(
        BotExecutionContext context,
        long jobId,
        int offset,
        ChatId chatId,
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        var photos = await context.Db.JobPhotos
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .OrderBy(x => x.Id)
            .Skip(Math.Max(0, offset))
            .Take(10)
            .Select(x => x.TelegramFileId)
            .ToListAsync(cancellationToken);

        if (photos.Count == 0)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                telegramUserId,
                chatId,
                "Sem fotos para exibir.",
                null,
                jobId,
                cancellationToken);

            return;
        }

        await _sender.SendMediaGroupAsync(
            context.Db,
            context.Bot,
            context.TenantId,
            telegramUserId,
            chatId,
            photos,
            $"Galeria do pedido #{jobId}",
            jobId,
            cancellationToken);

        var total = await context.Db.JobPhotos.CountAsync(x => x.JobId == jobId, cancellationToken);
        var nextOffset = Math.Max(0, offset) + photos.Count;
        if (nextOffset < total)
        {
            await _sender.SendTextAsync(
                context.Db,
                context.Bot,
                context.TenantId,
                telegramUserId,
                chatId,
                "Existem mais fotos desse pedido.",
                KeyboardFactory.GalleryNext($"J:{jobId}:GAL", nextOffset),
                jobId,
                cancellationToken);
        }
    }

    public static string PreferenceLabel(string? code)
    {
        return code?.Trim().ToUpperInvariant() switch
        {
            "LOW" => "Menor preco",
            "RAT" => "Melhor avaliados",
            "FAST" => "Mais rapido",
            "CHO" => "Escolher prestador",
            _ => "Nao informado"
        };
    }

    private static bool MatchesCategory(string category, string categoriesJson)
    {
        if (string.IsNullOrWhiteSpace(categoriesJson))
        {
            return true;
        }

        var normalizedCategory = NormalizeName(category);
        return categoriesJson.Contains(normalizedCategory, StringComparison.OrdinalIgnoreCase)
               || categoriesJson.Contains(category, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PassesRadiusFilter(Job job, ProviderProfile profile)
    {
        if (!job.Latitude.HasValue || !job.Longitude.HasValue || !profile.BaseLatitude.HasValue || !profile.BaseLongitude.HasValue)
        {
            return true;
        }

        var distance = DistanceKm(job.Latitude.Value, job.Longitude.Value, profile.BaseLatitude.Value, profile.BaseLongitude.Value);
        return distance <= Math.Max(1, profile.RadiusKm);
    }

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return r * c;
    }

    private static double ToRad(double value) => value * Math.PI / 180d;

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

    private static string NormalizeName(string value)
    {
        var safe = value.Trim().ToLowerInvariant();
        return safe
            .Replace(" ", "")
            .Replace("-", "");
    }
}
