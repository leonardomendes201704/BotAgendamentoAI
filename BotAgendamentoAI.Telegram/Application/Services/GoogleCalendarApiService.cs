using System.Globalization;
using BotAgendamentoAI.Telegram.Domain.Entities;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class GoogleCalendarApiService
{
    public async Task<string> UpsertEventAsync(
        TenantGoogleCalendarConfig config,
        GoogleCalendarEventPayload payload,
        string? existingEventId,
        CancellationToken cancellationToken)
    {
        var service = CreateCalendarService(config);
        var safeCalendarId = SafeCalendarId(config.CalendarId);
        var eventInput = BuildEvent(config, payload);

        if (!string.IsNullOrWhiteSpace(existingEventId))
        {
            try
            {
                var updateRequest = service.Events.Update(eventInput, safeCalendarId, existingEventId);
                updateRequest.SendUpdates = EventsResource.UpdateRequest.SendUpdatesEnum.None;
                var updated = await updateRequest.ExecuteAsync(cancellationToken);
                return string.IsNullOrWhiteSpace(updated.Id) ? existingEventId : updated.Id;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Event was removed externally; continue with insert.
            }
        }

        var insertRequest = service.Events.Insert(eventInput, safeCalendarId);
        insertRequest.SendUpdates = EventsResource.InsertRequest.SendUpdatesEnum.None;
        var created = await insertRequest.ExecuteAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(created.Id))
        {
            throw new InvalidOperationException("Google Calendar retornou evento sem Id.");
        }

        return created.Id;
    }

    public async Task DeleteEventAsync(
        TenantGoogleCalendarConfig config,
        string eventId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return;
        }

        var service = CreateCalendarService(config);
        var safeCalendarId = SafeCalendarId(config.CalendarId);

        try
        {
            var deleteRequest = service.Events.Delete(safeCalendarId, eventId);
            deleteRequest.SendUpdates = EventsResource.DeleteRequest.SendUpdatesEnum.None;
            await deleteRequest.ExecuteAsync(cancellationToken);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already removed.
        }
    }

    private static CalendarService CreateCalendarService(TenantGoogleCalendarConfig config)
    {
        var safeJson = (config.ServiceAccountJson ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(safeJson))
        {
            throw new InvalidOperationException("ServiceAccountJson nao configurado para Google Calendar.");
        }

        var credential = GoogleCredential
            .FromJson(safeJson)
            .CreateScoped(CalendarService.Scope.Calendar);

        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "BotAgendamentoAI.Telegram"
        });
    }

    private static Event BuildEvent(TenantGoogleCalendarConfig config, GoogleCalendarEventPayload payload)
    {
        var safeTimeZone = string.IsNullOrWhiteSpace(config.TimeZoneId)
            ? "America/Sao_Paulo"
            : config.TimeZoneId.Trim();

        return new Event
        {
            Summary = string.IsNullOrWhiteSpace(payload.Title) ? "Agendamento" : payload.Title,
            Description = payload.Description ?? string.Empty,
            Location = payload.Location ?? string.Empty,
            Start = new EventDateTime
            {
                DateTimeRaw = payload.StartLocal.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                TimeZone = safeTimeZone
            },
            End = new EventDateTime
            {
                DateTimeRaw = payload.EndLocal.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                TimeZone = safeTimeZone
            }
        };
    }

    private static string SafeCalendarId(string? calendarId)
    {
        var safe = (calendarId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(safe))
        {
            throw new InvalidOperationException("CalendarId nao configurado para Google Calendar.");
        }

        return safe;
    }
}

public sealed class GoogleCalendarEventPayload
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public DateTimeOffset StartLocal { get; set; }
    public DateTimeOffset EndLocal { get; set; }
}
