using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class ConversationsController : Controller
{
    private static readonly HttpClient ViaCepHttpClient = BuildViaCepHttpClient();
    private static readonly HttpClient GeocodeHttpClient = BuildGeocodeHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAdminRepository _repository;

    public ConversationsController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A", int limit = 100)
    {
        ViewData["Tenant"] = tenant;
        ViewData["Limit"] = Math.Clamp(limit, 1, 500);
        ViewData["Tenants"] = await _repository.GetTenantIdsAsync();
        var items = await _repository.GetConversationThreadsAsync(tenant, Math.Clamp(limit, 1, 500));
        return View(items);
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ActiveThreads(int limit = 150)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var tenants = (await _repository.GetTenantIdsAsync())
            .Where(static tenant => !string.IsNullOrWhiteSpace(tenant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tenants.Length == 0)
        {
            return Json(new
            {
                items = Array.Empty<object>(),
                total = 0,
                atUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        var perTenantLimit = Math.Clamp(safeLimit, 20, 200);
        var threadsByTenant = await Task.WhenAll(tenants.Select(async tenant => new
        {
            TenantId = tenant,
            Threads = await _repository.GetConversationThreadsAsync(tenant, perTenantLimit)
        }));

        var items = threadsByTenant
            .SelectMany(group => group.Threads.Select(thread => new
            {
                tenantId = group.TenantId,
                phone = thread.Phone,
                lastMessagePreview = thread.LastMessagePreview,
                menuContext = thread.MenuContext,
                isInHumanHandoff = thread.IsInHumanHandoff,
                isAwaitingHumanReply = thread.IsAwaitingHumanReply,
                lastMessageAtUtc = thread.LastMessageAtUtc
            }))
            .OrderByDescending(static item => item.lastMessageAtUtc)
            .Take(safeLimit)
            .Select(item => new
            {
                tenantId = item.tenantId,
                phone = item.phone,
                lastMessagePreview = item.lastMessagePreview,
                menuContext = item.menuContext,
                isInHumanHandoff = item.isInHumanHandoff,
                isAwaitingHumanReply = item.isAwaitingHumanReply,
                lastMessageAtUtc = item.lastMessageAtUtc.ToString("O", CultureInfo.InvariantCulture)
            })
            .ToList();

        var awaitingHumanReplyCount = items.Count(static item => item.isAwaitingHumanReply);

        return Json(new
        {
            items,
            total = items.Count,
            awaitingHumanReplyCount,
            atUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public async Task<IActionResult> Details(string tenant, string phone, int limit = 250)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return RedirectToAction(nameof(Index));
        }

        var safeLimit = Math.Clamp(limit, 1, 1000);
        var model = new ConversationDetailsViewModel
        {
            TenantId = tenant.Trim(),
            Phone = phone.Trim(),
            Handoff = await _repository.GetConversationHandoffStatusAsync(tenant, phone),
            Messages = await _repository.GetConversationMessagesAsync(tenant, phone, safeLimit)
        };
        ViewData["Limit"] = safeLimit;

        return View(model);
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Snapshot(string tenant, string phone, int limit = 250)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { error = "tenant e phone sao obrigatorios." });
        }

        var normalizedTenant = tenant.Trim();
        var normalizedPhone = phone.Trim();
        var messages = await _repository.GetConversationMessagesAsync(
            normalizedTenant,
            normalizedPhone,
            Math.Clamp(limit, 1, 1000));
        var handoff = await _repository.GetConversationHandoffStatusAsync(normalizedTenant, normalizedPhone);

        var visibleMessages = messages
            .Where(message => !string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            .Select(message => new
            {
                id = message.Id,
                direction = message.Direction,
                role = message.Role,
                content = message.Content,
                createdAtUtc = message.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            })
            .ToList();

        return Json(new
        {
            tenantId = normalizedTenant,
            phone = normalizedPhone,
            atUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            messages = visibleMessages,
            handoff = SerializeHandoff(handoff)
        });
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> OrderWizardBootstrap(string tenant, string phone)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { ok = false, error = "tenant e phone sao obrigatorios." });
        }

        var normalizedTenant = tenant.Trim();
        var normalizedPhone = phone.Trim();
        var context = await _repository.GetConversationOrderClientContextAsync(normalizedTenant, normalizedPhone);
        var categories = await _repository.GetCategoriesAsync(normalizedTenant);

        return Json(new
        {
            ok = true,
            context,
            categories = categories
                .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new
                {
                    name = item.Name
                }),
            preferences = new[]
            {
                new { code = "LOW", label = "Menor preco" },
                new { code = "RAT", label = "Melhor avaliados" },
                new { code = "FAST", label = "Mais rapido" },
                new { code = "CHO", label = "Escolher prestador" }
            }
        });
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ResolveCep(string cep)
    {
        var digits = NormalizeCepDigits(cep);
        if (digits.Length != 8)
        {
            return BadRequest(new { ok = false, error = "CEP invalido." });
        }

        var lookup = await LookupCepAsync(digits, HttpContext.RequestAborted);
        if (!lookup.IsSuccess)
        {
            return BadRequest(new { ok = false, error = lookup.Error });
        }

        var baseAddress = BuildAddressFromCep(lookup);
        var geocode = await TryGeocodeByCepAsync(digits, HttpContext.RequestAborted);
        if (!geocode.Success)
        {
            geocode = await TryGeocodeByAddressAsync(baseAddress, HttpContext.RequestAborted);
        }

        return Json(new
        {
            ok = true,
            cep = FormatCep(digits),
            street = lookup.Street,
            neighborhood = lookup.Neighborhood,
            city = lookup.City,
            state = lookup.State,
            baseAddress,
            latitude = geocode.Latitude,
            longitude = geocode.Longitude,
            geocodeOk = geocode.Success,
            geocodeError = geocode.Error
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateAssistedOrder([FromBody] AssistedOrderRequest? request)
    {
        if (request is null)
        {
            return BadRequest(new { ok = false, error = "Payload obrigatorio." });
        }

        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.Phone))
        {
            return BadRequest(new { ok = false, error = "tenant e phone sao obrigatorios." });
        }

        if (string.IsNullOrWhiteSpace(request.Category)
            || string.IsNullOrWhiteSpace(request.Description)
            || string.IsNullOrWhiteSpace(request.Cep)
            || string.IsNullOrWhiteSpace(request.Street)
            || string.IsNullOrWhiteSpace(request.Number)
            || string.IsNullOrWhiteSpace(request.Neighborhood)
            || string.IsNullOrWhiteSpace(request.City)
            || string.IsNullOrWhiteSpace(request.State)
            || string.IsNullOrWhiteSpace(request.PreferenceCode)
            || string.IsNullOrWhiteSpace(request.ContactName)
            || string.IsNullOrWhiteSpace(request.ContactPhone))
        {
            return BadRequest(new { ok = false, error = "Preencha todos os campos obrigatorios do pedido." });
        }

        var safeMode = (request.ScheduleMode ?? string.Empty).Trim().ToUpperInvariant();
        var isUrgent = false;
        DateTimeOffset? scheduledAt = null;
        switch (safeMode)
        {
            case "URG":
                isUrgent = true;
                scheduledAt = DateTimeOffset.UtcNow;
                break;

            case "TOD":
                scheduledAt = BuildTodaySuggestedSchedule();
                break;

            case "CAL":
                if (!TryParseLocalSchedule(request.ScheduledAtLocal, out var parsedScheduledAt))
                {
                    return BadRequest(new { ok = false, error = "Data/hora do agendamento invalida." });
                }

                scheduledAt = parsedScheduledAt;
                break;

            default:
                return BadRequest(new { ok = false, error = "Modo de agendamento invalido." });
        }

        var addressText = BuildFullAddress(
            request.Street,
            request.Number,
            request.Complement,
            request.Neighborhood,
            request.City,
            request.State,
            request.Cep);

        var command = new ConversationOrderDraftCommand
        {
            TenantId = request.TenantId.Trim(),
            Phone = request.Phone.Trim(),
            Category = request.Category.Trim(),
            Description = request.Description.Trim(),
            AddressText = addressText,
            Cep = NormalizeCepDigits(request.Cep),
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            IsUrgent = isUrgent,
            ScheduledAt = scheduledAt,
            PreferenceCode = request.PreferenceCode.Trim().ToUpperInvariant(),
            ContactName = request.ContactName.Trim(),
            ContactPhone = request.ContactPhone.Trim()
        };

        var result = await _repository.SendClientOrderConfirmationAsync(command, ResolveAgent());
        if (!result.Success)
        {
            return BadRequest(new
            {
                ok = false,
                error = string.IsNullOrWhiteSpace(result.Error) ? "Nao foi possivel enviar a confirmacao do pedido." : result.Error,
                handoff = SerializeHandoff(result.Handoff)
            });
        }

        return Json(new
        {
            ok = true,
            telegramMessageId = result.TelegramMessageId,
            handoff = SerializeHandoff(result.Handoff)
        });
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> HandoffStatus(string tenant, string phone)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { error = "tenant e phone sao obrigatorios." });
        }

        var status = await _repository.GetConversationHandoffStatusAsync(tenant, phone);
        return Json(new
        {
            tenantId = tenant.Trim(),
            phone = phone.Trim(),
            handoff = SerializeHandoff(status)
        });
    }

    [HttpPost]
    public async Task<IActionResult> HandoffOpen(string tenant, string phone)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { ok = false, error = "tenant e phone sao obrigatorios." });
        }

        var status = await _repository.OpenConversationHandoffAsync(tenant, phone, ResolveAgent());
        return Json(new
        {
            ok = true,
            tenantId = tenant.Trim(),
            phone = phone.Trim(),
            handoff = SerializeHandoff(status)
        });
    }

    [HttpPost]
    public async Task<IActionResult> HandoffClose(string tenant, string phone, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { ok = false, error = "tenant e phone sao obrigatorios." });
        }

        var status = await _repository.CloseConversationHandoffAsync(tenant, phone, ResolveAgent(), reason);
        return Json(new
        {
            ok = true,
            tenantId = tenant.Trim(),
            phone = phone.Trim(),
            handoff = SerializeHandoff(status)
        });
    }

    [HttpPost]
    public async Task<IActionResult> SendHumanMessage(string tenant, string phone, string message)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { ok = false, error = "tenant e phone sao obrigatorios." });
        }

        var result = await _repository.SendHumanMessageAsync(tenant, phone, message, ResolveAgent());
        if (!result.Success)
        {
            return BadRequest(new
            {
                ok = false,
                error = string.IsNullOrWhiteSpace(result.Error) ? "Nao foi possivel enviar a mensagem." : result.Error,
                handoff = SerializeHandoff(result.Handoff)
            });
        }

        return Json(new
        {
            ok = true,
            telegramMessageId = result.TelegramMessageId,
            handoff = SerializeHandoff(result.Handoff)
        });
    }

    private string ResolveAgent()
    {
        var identityName = User?.Identity?.Name?.Trim();
        return string.IsNullOrWhiteSpace(identityName) ? "admin" : identityName;
    }

    private static string NormalizeCepDigits(string? value)
        => new string((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string FormatCep(string? value)
    {
        var digits = NormalizeCepDigits(value);
        return digits.Length == 8 ? $"{digits[..5]}-{digits[5..]}" : digits;
    }

    private static string BuildAddressFromCep(CepLookupResult lookup)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(lookup.Street))
        {
            parts.Add(lookup.Street.Trim());
        }

        if (!string.IsNullOrWhiteSpace(lookup.Neighborhood))
        {
            parts.Add(lookup.Neighborhood.Trim());
        }

        var cityUf = string.Join(
            " - ",
            new[] { lookup.City?.Trim() ?? string.Empty, lookup.State?.Trim() ?? string.Empty }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(cityUf))
        {
            parts.Add(cityUf);
        }

        if (!string.IsNullOrWhiteSpace(lookup.Cep))
        {
            parts.Add($"CEP {FormatCep(lookup.Cep)}");
        }

        return string.Join(", ", parts.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildFullAddress(
        string street,
        string number,
        string? complement,
        string neighborhood,
        string city,
        string state,
        string cep)
    {
        var firstLine = string.Join(
            ", ",
            new[] { street?.Trim() ?? string.Empty, number?.Trim() ?? string.Empty, complement?.Trim() ?? string.Empty }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            parts.Add(firstLine);
        }

        if (!string.IsNullOrWhiteSpace(neighborhood))
        {
            parts.Add(neighborhood.Trim());
        }

        var cityUf = string.Join(
            " - ",
            new[] { city?.Trim() ?? string.Empty, state?.Trim() ?? string.Empty }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(cityUf))
        {
            parts.Add(cityUf);
        }

        var safeCep = FormatCep(cep);
        if (!string.IsNullOrWhiteSpace(safeCep))
        {
            parts.Add($"CEP {safeCep}");
        }

        return string.Join(", ", parts.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static TimeZoneInfo ResolveBusinessTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    private static DateTimeOffset BuildTodaySuggestedSchedule()
    {
        var timeZone = ResolveBusinessTimeZone();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var nextSlotLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
        if (nextSlotLocal.Hour < 9)
        {
            nextSlotLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, 9, 0, 0, DateTimeKind.Unspecified);
        }

        if (nextSlotLocal.Hour >= 21)
        {
            nextSlotLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, 9, 0, 0, DateTimeKind.Unspecified).AddDays(1);
        }

        return new DateTimeOffset(nextSlotLocal, timeZone.GetUtcOffset(nextSlotLocal));
    }

    private static bool TryParseLocalSchedule(string? value, out DateTimeOffset scheduledAt)
    {
        scheduledAt = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                value.Trim(),
                new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localValue))
        {
            return false;
        }

        var timeZone = ResolveBusinessTimeZone();
        var unspecified = DateTime.SpecifyKind(localValue, DateTimeKind.Unspecified);
        scheduledAt = new DateTimeOffset(unspecified, timeZone.GetUtcOffset(unspecified));
        return true;
    }

    private static async Task<CepLookupResult> LookupCepAsync(string digits, CancellationToken cancellationToken)
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

    private static async Task<GeocodeLookupResult> TryGeocodeByCepAsync(string digits, CancellationToken cancellationToken)
    {
        var awesome = await TryGeocodeByAwesomeCepAsync(digits, cancellationToken);
        if (awesome.Success)
        {
            return awesome;
        }

        var lookup = await LookupCepAsync(digits, cancellationToken);
        if (lookup.IsSuccess)
        {
            var byAddress = await TryGeocodeByAddressAsync(BuildAddressFromCep(lookup), cancellationToken);
            if (byAddress.Success)
            {
                return byAddress;
            }
        }

        return await TryGeocodeByAddressAsync(digits, cancellationToken);
    }

    private static async Task<GeocodeLookupResult> TryGeocodeByAddressAsync(string address, CancellationToken cancellationToken)
    {
        var safeAddress = (address ?? string.Empty).Trim();
        if (safeAddress.Length == 0)
        {
            return GeocodeLookupResult.Fail("Endereco vazio para geocode.");
        }

        var query = Uri.EscapeDataString($"{safeAddress}, Brasil");
        var endpoint = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=br&q={query}";

        try
        {
            using var response = await GeocodeHttpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GeocodeLookupResult.Fail($"HTTP {(int)response.StatusCode} no geocode.");
            }

            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payloadText);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() == 0)
            {
                return GeocodeLookupResult.Fail("Nenhum resultado no geocode.");
            }

            var first = document.RootElement[0];
            var latText = first.TryGetProperty("lat", out var latProp) ? latProp.GetString() : null;
            var lonText = first.TryGetProperty("lon", out var lonProp) ? lonProp.GetString() : null;
            if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                return GeocodeLookupResult.Fail("Lat/lng invalidos no geocode.");
            }

            return GeocodeLookupResult.Ok(latitude, longitude);
        }
        catch (Exception ex)
        {
            return GeocodeLookupResult.Fail(ex.Message);
        }
    }

    private static async Task<GeocodeLookupResult> TryGeocodeByAwesomeCepAsync(string digits, CancellationToken cancellationToken)
    {
        if (digits.Length != 8)
        {
            return GeocodeLookupResult.Fail("CEP invalido para geocode.");
        }

        try
        {
            using var response = await GeocodeHttpClient.GetAsync($"https://cep.awesomeapi.com.br/json/{digits}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GeocodeLookupResult.Fail($"HTTP {(int)response.StatusCode} na AwesomeAPI.");
            }

            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<AwesomeCepPayload>(payloadText, JsonOptions);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Lat)
                || string.IsNullOrWhiteSpace(payload.Lng))
            {
                return GeocodeLookupResult.Fail("AwesomeAPI sem lat/lng.");
            }

            if (!double.TryParse(payload.Lat.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.TryParse(payload.Lng.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                return GeocodeLookupResult.Fail("AwesomeAPI retornou lat/lng invalidos.");
            }

            return GeocodeLookupResult.Ok(latitude, longitude);
        }
        catch (Exception ex)
        {
            return GeocodeLookupResult.Fail(ex.Message);
        }
    }

    private static HttpClient BuildViaCepHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BotAgendamentoAI.Admin/1.0 (+lookup-cep)");
        return client;
    }

    private static HttpClient BuildGeocodeHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BotAgendamentoAI.Admin/1.0 (+nominatim-geocode)");
        return client;
    }

    private static object SerializeHandoff(ConversationHandoffStatus handoff)
    {
        return new
        {
            tenantId = handoff.TenantId,
            phone = handoff.Phone,
            isTelegramThread = handoff.IsTelegramThread,
            isOpen = handoff.IsOpen,
            requestedByRole = handoff.RequestedByRole,
            assignedAgent = handoff.AssignedAgent,
            previousState = handoff.PreviousState,
            closeReason = handoff.CloseReason,
            requestedAtUtc = handoff.RequestedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            acceptedAtUtc = handoff.AcceptedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            closedAtUtc = handoff.ClosedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            lastMessageAtUtc = handoff.LastMessageAtUtc?.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    public sealed class AssistedOrderRequest
    {
        public string TenantId { get; set; } = "A";
        public string Phone { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Cep { get; set; } = string.Empty;
        public string Street { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public string Complement { get; set; } = string.Empty;
        public string Neighborhood { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string ScheduleMode { get; set; } = "TOD";
        public string ScheduledAtLocal { get; set; } = string.Empty;
        public string PreferenceCode { get; set; } = "RAT";
        public string ContactName { get; set; } = string.Empty;
        public string ContactPhone { get; set; } = string.Empty;
    }

    private sealed class ViaCepPayload
    {
        public string Logradouro { get; set; } = string.Empty;
        public string Bairro { get; set; } = string.Empty;
        public string Localidade { get; set; } = string.Empty;
        public string Uf { get; set; } = string.Empty;
        public bool Erro { get; set; }
    }

    private sealed class AwesomeCepPayload
    {
        public string Lat { get; set; } = string.Empty;
        public string Lng { get; set; } = string.Empty;
    }

    private sealed class CepLookupResult
    {
        public bool IsSuccess { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public string Cep { get; private set; } = string.Empty;
        public string Street { get; private set; } = string.Empty;
        public string Neighborhood { get; private set; } = string.Empty;
        public string City { get; private set; } = string.Empty;
        public string State { get; private set; } = string.Empty;

        public static CepLookupResult Success(string cep, string? street, string? neighborhood, string? city, string? state)
        {
            return new CepLookupResult
            {
                IsSuccess = true,
                Cep = cep,
                Street = street?.Trim() ?? string.Empty,
                Neighborhood = neighborhood?.Trim() ?? string.Empty,
                City = city?.Trim() ?? string.Empty,
                State = state?.Trim().ToUpperInvariant() ?? string.Empty,
                Error = string.Empty
            };
        }

        public static CepLookupResult Fail(string? error)
        {
            return new CepLookupResult
            {
                IsSuccess = false,
                Error = string.IsNullOrWhiteSpace(error) ? "Falha ao consultar CEP." : error.Trim()
            };
        }
    }

    private sealed class GeocodeLookupResult
    {
        public bool Success { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public double? Latitude { get; private set; }
        public double? Longitude { get; private set; }

        public static GeocodeLookupResult Ok(double latitude, double longitude)
        {
            return new GeocodeLookupResult
            {
                Success = true,
                Latitude = latitude,
                Longitude = longitude,
                Error = string.Empty
            };
        }

        public static GeocodeLookupResult Fail(string? error)
        {
            return new GeocodeLookupResult
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(error) ? "Falha no geocode." : error.Trim()
            };
        }
    }
}
