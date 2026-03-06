using System.Globalization;
using BotAgendamentoAI.Telegram.Application.Services;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram;

public sealed class GoogleCalendarSyncWorker : BackgroundService
{
    private const int DefaultMaxAttempts = 8;
    private const int DefaultRetryBaseSeconds = 10;
    private const int DefaultRetryMaxSeconds = 600;
    private const int BatchSize = 20;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(4);

    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly GoogleCalendarApiService _calendarApi;
    private readonly AvailabilityService _availability;
    private readonly BotExceptionLogService _exceptionLog;
    private readonly ILogger<GoogleCalendarSyncWorker> _logger;

    public GoogleCalendarSyncWorker(
        IDbContextFactory<BotDbContext> dbFactory,
        GoogleCalendarApiService calendarApi,
        AvailabilityService availability,
        BotExceptionLogService exceptionLog,
        ILogger<GoogleCalendarSyncWorker> logger)
    {
        _dbFactory = dbFactory;
        _calendarApi = calendarApi;
        _availability = availability;
        _exceptionLog = exceptionLog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Google Calendar sync worker iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await ProcessPendingBatchAsync(stoppingToken);
            if (!processed)
            {
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessPendingBatchAsync(CancellationToken cancellationToken)
    {
        List<long> pendingIds;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            var candidates = await db.CalendarSyncQueue
                .AsNoTracking()
                .Where(x => x.Status == CalendarSyncQueueService.PendingStatus)
                .OrderBy(x => x.Id)
                .Take(BatchSize * 5)
                .ToListAsync(cancellationToken);

            pendingIds = candidates
                .Where(x => x.AvailableAtUtc <= now)
                .OrderBy(x => x.Id)
                .Take(BatchSize)
                .Select(x => x.Id)
                .ToList();
        }

        if (pendingIds.Count == 0)
        {
            return false;
        }

        foreach (var queueId in pendingIds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!await TryLockItemAsync(queueId, cancellationToken))
            {
                continue;
            }

            await ProcessLockedItemAsync(queueId, cancellationToken);
        }

        return true;
    }

    private async Task<bool> TryLockItemAsync(long queueId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.CalendarSyncQueue.FirstOrDefaultAsync(x => x.Id == queueId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (item.Status != CalendarSyncQueueService.PendingStatus || item.AvailableAtUtc > now)
        {
            return false;
        }

        item.Status = CalendarSyncQueueService.ProcessingStatus;
        item.LockedAtUtc = now;
        item.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ProcessLockedItemAsync(long queueId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.CalendarSyncQueue.FirstOrDefaultAsync(x => x.Id == queueId, cancellationToken);
        if (item is null || item.Status != CalendarSyncQueueService.ProcessingStatus)
        {
            return;
        }

        var tenantConfig = await db.TenantGoogleCalendarConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == item.TenantId, cancellationToken);
        var retryPolicy = RetryPolicy.FromConfig(tenantConfig);

        try
        {
            await ProcessLockedItemCoreAsync(db, item, tenantConfig, cancellationToken);
            item.Attempts += 1;
            item.Status = CalendarSyncQueueService.DoneStatus;
            item.LockedAtUtc = null;
            item.LastError = string.Empty;
            item.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            var nextAttempt = item.Attempts + 1;

            item.Attempts = nextAttempt;
            item.LockedAtUtc = null;
            item.LastError = TrimError(ex.Message);
            item.UpdatedAtUtc = now;
            if (nextAttempt >= retryPolicy.MaxAttempts)
            {
                item.Status = CalendarSyncQueueService.FailedStatus;
                item.AvailableAtUtc = now;
            }
            else
            {
                item.Status = CalendarSyncQueueService.PendingStatus;
                item.AvailableAtUtc = now.Add(CalculateBackoff(nextAttempt, retryPolicy));
            }

            await _exceptionLog.TryLogAsync(
                db,
                item.TenantId,
                "GoogleCalendarSyncWorker.ProcessLockedItemAsync",
                ex,
                null,
                null,
                item.JobId,
                $"queueId={item.Id};action={item.Action};attempt={nextAttempt}",
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                ex,
                "Falha no sync Google Calendar. queueId={QueueId} tenant={Tenant} jobId={JobId} action={Action} attempt={Attempt}",
                item.Id,
                item.TenantId,
                item.JobId,
                item.Action,
                nextAttempt);
        }
    }

    private async Task ProcessLockedItemCoreAsync(
        BotDbContext db,
        CalendarSyncQueueItem item,
        TenantGoogleCalendarConfig? config,
        CancellationToken cancellationToken)
    {
        if (config is null || !config.IsEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.CalendarId) || string.IsNullOrWhiteSpace(config.ServiceAccountJson))
        {
            throw new InvalidOperationException($"Google Calendar incompleto para tenant {item.TenantId}. Configure Calendar ID e Service Account JSON.");
        }

        var job = await db.Jobs
            .AsNoTracking()
            .Include(x => x.ClientUser)
            .Include(x => x.ProviderUser)
            .FirstOrDefaultAsync(x => x.Id == item.JobId && x.TenantId == item.TenantId, cancellationToken);

        var link = await db.JobCalendarLinks
            .FirstOrDefaultAsync(x => x.JobId == item.JobId && x.TenantId == item.TenantId, cancellationToken);

        var cancelAction = string.Equals(item.Action, CalendarSyncQueueService.CancelAction, StringComparison.OrdinalIgnoreCase);
        if (cancelAction || job?.Status == JobStatus.Cancelled)
        {
            if (job is null)
            {
                await CancelEventAsync(db, config, link, cancellationToken);
                return;
            }

            if (link is null)
            {
                return;
            }

            var cancelPayload = BuildPayload(job, config);
            var cancelledEventId = await _calendarApi.UpsertEventAsync(
                config,
                cancelPayload,
                link.CalendarEventId,
                cancellationToken);
            link.CalendarEventId = cancelledEventId;
            link.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (job is null)
        {
            await CancelEventAsync(db, config, link, cancellationToken);
            return;
        }

        var rules = AvailabilityRules.FromConfig(config);
        var check = await _availability.CheckSlotAvailabilityAsync(
            db,
            new AvailabilityRequest
            {
                TenantId = job.TenantId,
                ClientUserId = job.ClientUserId,
                ProviderUserId = job.ProviderUserId,
                ExcludeJobId = job.Id,
                Rules = rules,
                TimeZone = ResolveTimeZone(config.TimeZoneId),
                NowLocal = DateTimeOffset.UtcNow,
                RequireFutureSlotsOnly = false
            },
            job.ScheduledAt ?? job.CreatedAt,
            cancellationToken);
        if (!check.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Conflito de agenda detectado para job {job.Id}. Evento do Google Calendar nao sera criado/atualizado.");
        }

        var payload = BuildPayload(job, config);
        var eventId = await _calendarApi.UpsertEventAsync(config, payload, link?.CalendarEventId, cancellationToken);
        if (link is null)
        {
            db.JobCalendarLinks.Add(new JobCalendarLink
            {
                JobId = job.Id,
                TenantId = job.TenantId,
                CalendarEventId = eventId,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            link.CalendarEventId = eventId;
            link.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task CancelEventAsync(
        BotDbContext db,
        TenantGoogleCalendarConfig config,
        JobCalendarLink? link,
        CancellationToken cancellationToken)
    {
        if (link is null)
        {
            return;
        }

        await _calendarApi.DeleteEventAsync(config, link.CalendarEventId, cancellationToken);
        db.JobCalendarLinks.Remove(link);
    }

    private static GoogleCalendarEventPayload BuildPayload(Job job, TenantGoogleCalendarConfig config)
    {
        var timeZone = ResolveTimeZone(config.TimeZoneId);
        var startLocal = TimeZoneInfo.ConvertTime(job.ScheduledAt ?? job.CreatedAt, timeZone);
        var durationMinutes = Math.Clamp(config.DefaultDurationMinutes, 15, 720);
        var endLocal = startLocal.AddMinutes(durationMinutes);

        var tokens = BuildTemplateTokens(job, startLocal, endLocal);
        var titleTemplate = string.IsNullOrWhiteSpace(config.EventTitleTemplate)
            ? "Agendamento #{job_id} - {category}"
            : config.EventTitleTemplate;
        var descriptionTemplate = string.IsNullOrWhiteSpace(config.EventDescriptionTemplate)
            ? "Cliente: {client_name}\nTelefone: {client_phone}\nCategoria: {category}\nDescricao: {description}\nStatus: {status}\nQuando: {scheduled_at_local}\nEndereco: {address}\nTenant: {tenant_id}\nJobId: {job_id}"
            : config.EventDescriptionTemplate;

        return new GoogleCalendarEventPayload
        {
            Title = ApplyTemplate(titleTemplate, tokens),
            Description = ApplyTemplate(descriptionTemplate, tokens),
            Location = job.AddressText ?? string.Empty,
            ColorId = MapGoogleColorId(job.Status),
            StartLocal = startLocal,
            EndLocal = endLocal
        };
    }

    private static string MapGoogleColorId(JobStatus status)
    {
        return status switch
        {
            JobStatus.Requested => "5",
            JobStatus.WaitingProvider => "5",
            JobStatus.Accepted => "9",
            JobStatus.OnTheWay => "7",
            JobStatus.Arrived => "6",
            JobStatus.InProgress => "10",
            JobStatus.Finished => "2",
            JobStatus.Cancelled => "11",
            JobStatus.Draft => "8",
            _ => "8"
        };
    }

    private static Dictionary<string, string> BuildTemplateTokens(Job job, DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var clientPhone = string.IsNullOrWhiteSpace(job.ClientUser?.Phone)
            ? job.ClientUser?.TelegramUserId.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            : job.ClientUser.Phone!;
        var providerName = string.IsNullOrWhiteSpace(job.ProviderUser?.Name)
            ? "Aguardando atribuicao"
            : job.ProviderUser.Name;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["job_id"] = job.Id.ToString(CultureInfo.InvariantCulture),
            ["tenant_id"] = job.TenantId ?? "A",
            ["category"] = job.Category ?? string.Empty,
            ["description"] = job.Description ?? string.Empty,
            ["status"] = FormatJobStatus(job.Status),
            ["client_name"] = job.ClientUser?.Name ?? string.Empty,
            ["client_phone"] = clientPhone,
            ["provider_name"] = providerName,
            ["address"] = job.AddressText ?? string.Empty,
            ["scheduled_at_local"] = startLocal.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            ["ends_at_local"] = endLocal.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
        };
    }

    private static string ApplyTemplate(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var output = template ?? string.Empty;
        foreach (var token in tokens)
        {
            output = output.Replace($"{{{token.Key}}}", token.Value, StringComparison.OrdinalIgnoreCase);
        }

        return output;
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

    private static string TrimError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Erro nao informado.";
        }

        var safe = value.Trim();
        return safe.Length <= 2048 ? safe : safe[..2048];
    }

    private static TimeSpan CalculateBackoff(int attempt, RetryPolicy policy)
    {
        var safeAttempt = Math.Max(1, attempt);
        var exponent = Math.Min(safeAttempt - 1, 10);
        var rawSeconds = policy.RetryBaseSeconds * Math.Pow(2, exponent);
        var bounded = Math.Min(policy.RetryMaxSeconds, rawSeconds);
        return TimeSpan.FromSeconds(Math.Max(1, bounded));
    }

    private static TimeZoneInfo ResolveTimeZone(string? preferredId)
    {
        var safeId = string.IsNullOrWhiteSpace(preferredId) ? "America/Sao_Paulo" : preferredId.Trim();

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(safeId);
        }
        catch (TimeZoneNotFoundException) when (OperatingSystem.IsWindows() && safeId == "America/Sao_Paulo")
        {
            return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private readonly record struct RetryPolicy(int MaxAttempts, int RetryBaseSeconds, int RetryMaxSeconds)
    {
        public static RetryPolicy FromConfig(TenantGoogleCalendarConfig? config)
        {
            var maxAttempts = Math.Clamp(config?.MaxAttempts ?? DefaultMaxAttempts, 1, 30);
            var retryBaseSeconds = Math.Clamp(config?.RetryBaseSeconds ?? DefaultRetryBaseSeconds, 5, 600);
            var retryMaxSeconds = Math.Clamp(config?.RetryMaxSeconds ?? DefaultRetryMaxSeconds, 10, 86400);
            if (retryMaxSeconds < retryBaseSeconds)
            {
                retryMaxSeconds = retryBaseSeconds;
            }

            return new RetryPolicy(maxAttempts, retryBaseSeconds, retryMaxSeconds);
        }
    }
}
