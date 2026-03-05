using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotAgendamentoAI.Domain;

namespace BotAgendamentoAI.Bot;

public sealed class StateManager
{
    private readonly TimeZoneInfo _timeZone;

    public StateManager(TimeZoneInfo timeZone)
    {
        _timeZone = timeZone;
    }

    public ConversationSlots ParseSlots(string? slotsJson)
    {
        if (string.IsNullOrWhiteSpace(slotsJson))
        {
            return CreateEmptySlots();
        }

        try
        {
            var slots = JsonSerializer.Deserialize<ConversationSlots>(slotsJson, JsonDefaults.Options);
            Normalize(slots);
            return slots!;
        }
        catch
        {
            return CreateEmptySlots();
        }
    }

    public string SerializeSlots(ConversationSlots slots)
    {
        Normalize(slots);
        return JsonSerializer.Serialize(slots, JsonDefaults.Options);
    }

    public ConversationSlots ApplyUserMessage(ConversationSlots slots, string userText, DateTimeOffset nowUtc)
    {
        Normalize(slots);
        slots.LastSeenAtUtc = nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        var normalizedText = NormalizeText(userText);
        if (string.IsNullOrWhiteSpace(slots.CustomerName))
        {
            var extractedName = TryExtractName(userText);
            if (!string.IsNullOrWhiteSpace(extractedName))
            {
                slots.CustomerName = extractedName;
            }
        }

        if (string.IsNullOrWhiteSpace(slots.CustomerPhone))
        {
            var extractedPhone = TryExtractPhone(userText);
            if (!string.IsNullOrWhiteSpace(extractedPhone))
            {
                slots.CustomerPhone = extractedPhone;
            }
        }

        var cepMatch = Regex.Match(normalizedText, @"\b\d{5}-?\d{3}\b");
        if (cepMatch.Success)
        {
            slots.Cep = NormalizeCep(cepMatch.Value);
        }

        if (string.IsNullOrWhiteSpace(slots.ServiceTitle))
        {
            var inferredService = InferServiceTitle(normalizedText);
            if (!string.IsNullOrWhiteSpace(inferredService))
            {
                slots.ServiceTitle = inferredService;
            }
        }

        if (string.IsNullOrWhiteSpace(slots.ServiceCategory))
        {
            var inferredCategory = ServiceCategoryRules.InferCategoryFromText(userText);
            if (!string.IsNullOrWhiteSpace(inferredCategory))
            {
                slots.ServiceCategory = inferredCategory;
            }
        }

        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, _timeZone).DateTime;
        if (TryExtractDateTime(userText, nowLocal, out var desiredDateTime))
        {
            slots.DesiredDateTimeLocal = desiredDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        CaptureAddressHints(userText, slots.Address);

        var pending = InferPending(normalizedText);
        if (!string.IsNullOrWhiteSpace(pending))
        {
            slots.Pending = pending;
        }

        RecalculateMissingFields(slots);
        return slots;
    }

    public bool TryParseDateTimeFromMessage(string userText, DateTimeOffset nowUtc, out DateTime dateTimeLocal)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, _timeZone).DateTime;
        return TryExtractDateTime(userText, nowLocal, out dateTimeLocal);
    }

    public ConversationSlots ApplyToolResult(
        ConversationSlots slots,
        string toolName,
        string toolResultJson,
        DateTimeOffset nowUtc)
    {
        Normalize(slots);
        slots.LastSeenAtUtc = nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            if (!doc.RootElement.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
            {
                RecalculateMissingFields(slots);
                return slots;
            }

            if (toolName == "lookup_cep")
            {
                var cep = GetJsonString(doc.RootElement, "cep");
                if (!string.IsNullOrWhiteSpace(cep))
                {
                    slots.Cep = NormalizeCep(cep);
                }

                slots.Address.Logradouro = FirstNonEmpty(slots.Address.Logradouro, GetJsonString(doc.RootElement, "logradouro"));
                slots.Address.Bairro = FirstNonEmpty(slots.Address.Bairro, GetJsonString(doc.RootElement, "bairro"));
                slots.Address.Cidade = FirstNonEmpty(slots.Address.Cidade, GetJsonString(doc.RootElement, "localidade"));
                slots.Address.Uf = FirstNonEmpty(slots.Address.Uf, GetJsonString(doc.RootElement, "uf"));
                slots.Address.Complemento = FirstNonEmpty(slots.Address.Complemento, GetJsonString(doc.RootElement, "complemento"));

                if (!string.IsNullOrWhiteSpace(slots.Address.Logradouro) && string.IsNullOrWhiteSpace(slots.Address.Numero))
                {
                    slots.Pending = "awaiting_address_number";
                }
            }
            else if (toolName == "create_booking")
            {
                if (doc.RootElement.TryGetProperty("booking", out var bookingElement))
                {
                    slots.LastBookingId = GetJsonString(bookingElement, "id");
                    slots.ServiceCategory = FirstNonEmpty(slots.ServiceCategory, GetJsonString(bookingElement, "serviceCategory"));
                }

                slots.Pending = "booking_created";
            }
            else if (toolName == "cancel_booking")
            {
                slots.Pending = "booking_cancelled";
            }
            else if (toolName == "list_bookings")
            {
                slots.Pending = "bookings_listed";
            }
            else if (toolName == "reschedule_booking")
            {
                if (doc.RootElement.TryGetProperty("booking", out var bookingElement))
                {
                    slots.LastBookingId = GetJsonString(bookingElement, "id");
                    slots.DesiredDateTimeLocal = GetJsonString(bookingElement, "startLocal");
                }

                slots.Pending = "booking_rescheduled";
            }
        }
        catch
        {
            // Keep state as-is on malformed tool result.
        }

        RecalculateMissingFields(slots);
        return slots;
    }

    public string BuildSummary(string previousSummary, ConversationSlots slots, string userText, string? assistantText)
    {
        Normalize(slots);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(slots.Pending))
        {
            parts.Add($"pending={slots.Pending}");
        }

        if (!string.IsNullOrWhiteSpace(slots.ServiceTitle))
        {
            parts.Add($"service={slots.ServiceTitle}");
        }

        if (!string.IsNullOrWhiteSpace(slots.ServiceCategory))
        {
            parts.Add($"category={slots.ServiceCategory}");
        }

        if (!string.IsNullOrWhiteSpace(slots.CustomerName))
        {
            parts.Add($"customerName={slots.CustomerName}");
        }

        if (!string.IsNullOrWhiteSpace(slots.CustomerPhone))
        {
            parts.Add($"customerPhone={slots.CustomerPhone}");
        }

        if (!string.IsNullOrWhiteSpace(slots.DesiredDateTimeLocal))
        {
            parts.Add($"datetime={slots.DesiredDateTimeLocal}");
        }

        if (!string.IsNullOrWhiteSpace(slots.Cep))
        {
            parts.Add($"cep={slots.Cep}");
        }

        if (!string.IsNullOrWhiteSpace(slots.LastBookingId))
        {
            parts.Add($"lastBookingId={slots.LastBookingId}");
        }

        if (slots.MissingFields.Count > 0)
        {
            parts.Add($"missing={string.Join(",", slots.MissingFields)}");
        }

        if (!string.IsNullOrWhiteSpace(slots.LastSeenAtUtc))
        {
            parts.Add($"lastSeenAtUtc={slots.LastSeenAtUtc}");
        }

        if (!string.IsNullOrWhiteSpace(userText))
        {
            parts.Add($"lastUser=\"{TrimText(userText, 80)}\"");
        }

        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            parts.Add($"lastAssistant=\"{TrimText(assistantText, 80)}\"");
        }

        if (parts.Count == 0)
        {
            return string.IsNullOrWhiteSpace(previousSummary) ? "Sem contexto anterior." : previousSummary;
        }

        return string.Join("; ", parts);
    }

    public string BuildStatePromptBlock(string summary, ConversationSlots slots, bool returningAfter24h)
    {
        Normalize(slots);

        var prettyJson = JsonSerializer.Serialize(slots, new JsonSerializerOptions(JsonDefaults.Options)
        {
            WriteIndented = true
        });

        return $"""
ConversationState:
summary:
{summary}

slots_json:
{prettyJson}

pending: {slots.Pending ?? "none"}
returning_after_24h: {returningAfter24h.ToString().ToLowerInvariant()}

Rules:
- Use state/slots to avoid repeating questions.
- If user sent CEP, call lookup_cep and then ask only numero/complemento when missing.
- Before creating booking, confirm customerName and customerPhone.
- Before creating booking, set categoryName using a specific category. Never use "Outros".
- If menu is active, follow menu context and do not ask unrelated questions.
- For list_bookings, do not require name, phone, or date range unless the user explicitly asks for a period.
- If user asks for all agendamentos, call list_bookings without date filters.
- If serviceTitle and desiredDateTimeLocal already exist, ask only remaining missing fields.
- If returning_after_24h is true, send a retomada message and confirm intent before assuming old details.
""";
    }

    private static ConversationSlots CreateEmptySlots()
    {
        var slots = new ConversationSlots();
        Normalize(slots);
        return slots;
    }

    private static void Normalize(ConversationSlots? slots)
    {
        if (slots is null)
        {
            return;
        }

        slots.Address ??= new AddressSlots();
        slots.MissingFields ??= new List<string>();
        RecalculateMissingFields(slots);
    }

    private static void RecalculateMissingFields(ConversationSlots slots)
    {
        var menuContext = (slots.MenuContext ?? string.Empty).Trim().ToLowerInvariant();
        if (menuContext is "awaiting_menu_choice" or "awaiting_cancel_selection" or "awaiting_reschedule_selection" or
            "awaiting_reschedule_datetime" or "human_handoff" or "closed")
        {
            slots.MissingFields = new List<string>();
            return;
        }

        var operation = slots.Pending ?? string.Empty;
        var isListFlow = operation is "list_bookings" or "bookings_listed";
        if (isListFlow)
        {
            slots.MissingFields = new List<string>();
            return;
        }

        var isCancelFlow = operation is "cancel_booking" or "booking_cancelled";
        if (isCancelFlow)
        {
            slots.MissingFields = string.IsNullOrWhiteSpace(slots.LastBookingId)
                ? new List<string> { "bookingId" }
                : new List<string>();
            return;
        }

        var isRescheduleFlow = operation is "reschedule_booking" or "booking_rescheduled";
        if (isRescheduleFlow)
        {
            var missingReschedule = new List<string>();
            if (string.IsNullOrWhiteSpace(slots.LastBookingId))
            {
                missingReschedule.Add("bookingId");
            }

            if (string.IsNullOrWhiteSpace(slots.DesiredDateTimeLocal))
            {
                missingReschedule.Add("desiredDateTimeLocal");
            }

            slots.MissingFields = missingReschedule;
            return;
        }

        var isCreateFlow =
            operation is "create_booking" or "awaiting_address_number" or "booking_created" ||
            !string.IsNullOrWhiteSpace(slots.ServiceTitle) ||
            !string.IsNullOrWhiteSpace(slots.DesiredDateTimeLocal) ||
            !string.IsNullOrWhiteSpace(slots.Cep) ||
            !string.IsNullOrWhiteSpace(slots.Address.Logradouro);

        if (!isCreateFlow)
        {
            slots.MissingFields = new List<string>();
            return;
        }

        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(slots.ServiceTitle))
        {
            missing.Add("serviceTitle");
        }

        if (string.IsNullOrWhiteSpace(slots.CustomerName))
        {
            missing.Add("customerName");
        }

        if (string.IsNullOrWhiteSpace(slots.CustomerPhone))
        {
            missing.Add("customerPhone");
        }

        if (string.IsNullOrWhiteSpace(slots.DesiredDateTimeLocal))
        {
            missing.Add("desiredDateTimeLocal");
        }

        var hasMainAddress =
            !string.IsNullOrWhiteSpace(slots.Address.Logradouro) &&
            !string.IsNullOrWhiteSpace(slots.Address.Bairro) &&
            !string.IsNullOrWhiteSpace(slots.Address.Cidade) &&
            !string.IsNullOrWhiteSpace(slots.Address.Uf);

        if (!hasMainAddress)
        {
            missing.Add("address");
        }
        else if (string.IsNullOrWhiteSpace(slots.Address.Numero))
        {
            missing.Add("address.numero");
        }

        slots.MissingFields = missing;
    }

    private static void CaptureAddressHints(string input, AddressSlots address)
    {
        var normalized = NormalizeText(input);

        if (string.IsNullOrWhiteSpace(address.Numero))
        {
            var explicitNumber = Regex.Match(normalized, @"(?:numero|num|n[\.o]?)\s*(\d{1,6}[a-z]?)");
            if (explicitNumber.Success)
            {
                address.Numero = explicitNumber.Groups[1].Value;
            }
            else
            {
                var commaNumber = Regex.Match(input, @",\s*(\d{1,6}[A-Za-z]?)\b");
                if (commaNumber.Success)
                {
                    address.Numero = commaNumber.Groups[1].Value;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(address.Complemento))
        {
            var complement = Regex.Match(normalized, @"(?:apto|apt|bloco|casa|fundos|sala|complemento)\s*([a-z0-9\-\/ ]{1,30})");
            if (complement.Success)
            {
                address.Complemento = complement.Value.Trim();
            }
        }
    }

    private static string? InferPending(string normalizedText)
    {
        if (normalizedText.Contains("cancel", StringComparison.Ordinal))
        {
            return "cancel_booking";
        }

        if (normalizedText.Contains("alterar", StringComparison.Ordinal) ||
            normalizedText.Contains("reagendar", StringComparison.Ordinal) ||
            normalizedText.Contains("remarcar", StringComparison.Ordinal))
        {
            return "reschedule_booking";
        }

        if (normalizedText.Contains("listar", StringComparison.Ordinal) ||
            normalizedText.Contains("lista", StringComparison.Ordinal) ||
            normalizedText.Contains("quais", StringComparison.Ordinal) ||
            normalizedText.Contains("consultar", StringComparison.Ordinal) ||
            normalizedText.Contains("meus agendamentos", StringComparison.Ordinal))
        {
            return "list_bookings";
        }

        if (normalizedText.Contains("agendar", StringComparison.Ordinal) ||
            normalizedText.Contains("marcar", StringComparison.Ordinal) ||
            normalizedText.Contains("servico", StringComparison.Ordinal))
        {
            return "create_booking";
        }

        return null;
    }

    private static string? InferServiceTitle(string normalizedText)
    {
        if (normalizedText.Contains("torneira", StringComparison.Ordinal) ||
            normalizedText.Contains("vazamento", StringComparison.Ordinal) ||
            normalizedText.Contains("encan", StringComparison.Ordinal))
        {
            return "Conserto hidraulico";
        }

        if (normalizedText.Contains("eletric", StringComparison.Ordinal) ||
            normalizedText.Contains("tomada", StringComparison.Ordinal) ||
            normalizedText.Contains("disjuntor", StringComparison.Ordinal))
        {
            return "Reparo eletrico";
        }

        if (normalizedText.Contains("ar condicionado", StringComparison.Ordinal))
        {
            return "Servico de ar condicionado";
        }

        if (normalizedText.Contains("jardim", StringComparison.Ordinal) ||
            normalizedText.Contains("grama", StringComparison.Ordinal) ||
            normalizedText.Contains("poda", StringComparison.Ordinal))
        {
            return "Jardinagem";
        }

        if (normalizedText.Contains("limpeza", StringComparison.Ordinal))
        {
            return "Limpeza";
        }

        if (normalizedText.Contains("montagem", StringComparison.Ordinal) ||
            normalizedText.Contains("movel", StringComparison.Ordinal))
        {
            return "Montagem de moveis";
        }

        if (normalizedText.Contains("piscina", StringComparison.Ordinal))
        {
            return "Manutencao de piscina";
        }

        if (normalizedText.Contains("serralher", StringComparison.Ordinal))
        {
            return "Serralheria";
        }

        if (normalizedText.Contains("marcenar", StringComparison.Ordinal))
        {
            return "Marcenaria";
        }

        if (normalizedText.Contains("telhad", StringComparison.Ordinal))
        {
            return "Telhadista";
        }

        if (normalizedText.Contains("azulej", StringComparison.Ordinal))
        {
            return "Azulejista";
        }

        return null;
    }

    private static bool TryExtractDateTime(string input, DateTime nowLocal, out DateTime dateTimeLocal)
    {
        var normalized = NormalizeText(input);

        var yyyyMmDd = Regex.Match(
            normalized,
            @"\b(\d{4})[/-](\d{1,2})[/-](\d{1,2})(?:\D+([01]?\d|2[0-3])[:h]([0-5]\d))?\b");

        if (yyyyMmDd.Success && yyyyMmDd.Groups[4].Success)
        {
            var year = int.Parse(yyyyMmDd.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(yyyyMmDd.Groups[2].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(yyyyMmDd.Groups[3].Value, CultureInfo.InvariantCulture);
            var hour = int.Parse(yyyyMmDd.Groups[4].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(yyyyMmDd.Groups[5].Value, CultureInfo.InvariantCulture);

            if (TryBuildDate(year, month, day, hour, minute, out dateTimeLocal))
            {
                return true;
            }
        }

        var ddMm = Regex.Match(
            normalized,
            @"\b(\d{1,2})[/-](\d{1,2})(?:[/-](\d{2,4}))?(?:\D+([01]?\d|2[0-3])[:h]([0-5]\d))?\b");

        if (ddMm.Success && ddMm.Groups[4].Success)
        {
            var day = int.Parse(ddMm.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(ddMm.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = nowLocal.Year;

            if (ddMm.Groups[3].Success)
            {
                year = int.Parse(ddMm.Groups[3].Value, CultureInfo.InvariantCulture);
                if (year < 100)
                {
                    year += 2000;
                }
            }

            var hour = int.Parse(ddMm.Groups[4].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(ddMm.Groups[5].Value, CultureInfo.InvariantCulture);

            if (TryBuildDate(year, month, day, hour, minute, out dateTimeLocal))
            {
                return true;
            }
        }

        var timeOnly = Regex.Match(normalized, @"\b([01]?\d|2[0-3])[:h]([0-5]\d)\b");
        if (timeOnly.Success)
        {
            var hour = int.Parse(timeOnly.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(timeOnly.Groups[2].Value, CultureInfo.InvariantCulture);

            if (normalized.Contains("amanha", StringComparison.Ordinal))
            {
                var target = nowLocal.Date.AddDays(1);
                dateTimeLocal = new DateTime(target.Year, target.Month, target.Day, hour, minute, 0);
                return true;
            }

            if (normalized.Contains("hoje", StringComparison.Ordinal))
            {
                var target = nowLocal.Date;
                dateTimeLocal = new DateTime(target.Year, target.Month, target.Day, hour, minute, 0);
                return true;
            }
        }

        dateTimeLocal = default;
        return false;
    }

    private static bool TryBuildDate(int year, int month, int day, int hour, int minute, out DateTime value)
    {
        try
        {
            value = new DateTime(year, month, day, hour, minute, 0);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    private static string NormalizeText(string input)
    {
        var lowered = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lowered.Length);

        foreach (var c in lowered)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeCep(string rawCep)
    {
        var digits = Regex.Replace(rawCep, @"\D", string.Empty);
        if (digits.Length != 8)
        {
            return rawCep.Trim();
        }

        return $"{digits[..5]}-{digits[5..]}";
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string? FirstNonEmpty(string? current, string candidate)
    {
        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        return string.IsNullOrWhiteSpace(candidate) ? current : candidate;
    }

    private static string TrimText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : $"{text[..maxLength]}...";
    }

    private static string? TryExtractName(string input)
    {
        var match = Regex.Match(
            input.Trim(),
            @"(?i)\b(?:meu nome e|me chamo|sou)\s+([\p{L}][\p{L}' ]{1,50})");

        if (match.Success)
        {
            var rawFromPattern = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(rawFromPattern))
            {
                return rawFromPattern;
            }
        }

        var compact = input.Trim();
        if (compact.Length is >= 2 and <= 40 &&
            Regex.IsMatch(compact, @"^[\p{L}' ]+$") &&
            compact.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4)
        {
            return compact;
        }

        return null;
    }

    private static string? TryExtractPhone(string input)
    {
        var digits = Regex.Replace(input, @"\D", string.Empty);
        if (digits.Length < 10 || digits.Length > 13)
        {
            return null;
        }

        return digits;
    }
}
