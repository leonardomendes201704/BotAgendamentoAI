using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotAgendamentoAI.Domain;
using OpenAI.Chat;

namespace BotAgendamentoAI.Bot;

public sealed class SecretaryTools
{
    private readonly IBookingStore _store;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<ChatTool> _definitions;

    public SecretaryTools(IBookingStore store, HttpClient? httpClient = null)
    {
        _store = store;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _definitions = BuildDefinitions();
    }

    public IReadOnlyList<ChatTool> GetToolDefinitions() => _definitions;

    public async Task<ToolResult> ExecuteAsync(string toolName, string argsJson, IncomingMessage incoming)
    {
        return toolName switch
        {
            "create_booking" => ExecCreate(argsJson, incoming),
            "list_bookings" => ExecList(argsJson, incoming),
            "cancel_booking" => ExecCancel(argsJson, incoming),
            "reschedule_booking" => ExecReschedule(argsJson, incoming),
            "lookup_cep" => await ExecLookupCepAsync(argsJson),
            _ => BuildError("Tool desconhecida.")
        };
    }

    public IReadOnlyList<Booking> ListForCustomer(
        string tenantId,
        string customerPhone,
        DateTime? from = null,
        DateTime? to = null)
    {
        return _store.List(tenantId.Trim(), customerPhone.Trim(), from, to);
    }

    public bool CancelById(string tenantId, string bookingId)
    {
        return _store.Cancel(tenantId.Trim(), bookingId.Trim());
    }

    public Booking? RescheduleById(string tenantId, string bookingId, DateTime newStartLocal)
    {
        return _store.Reschedule(tenantId.Trim(), bookingId.Trim(), newStartLocal);
    }

    public IReadOnlyList<string> GetCategoryNames(string tenantId)
    {
        return _store.GetCategories(tenantId.Trim())
            .Select(category => category.Name)
            .OrderBy(name => name)
            .ToList();
    }

    private IReadOnlyList<ChatTool> BuildDefinitions()
    {
        var createBookingTool = ChatTool.CreateFunctionTool(
            functionName: "create_booking",
            functionDescription: "Cria um agendamento. categoryName deve ser uma categoria especifica do servico; nunca use 'Outros'.",
            functionParameters: BinaryData.FromString(
                """
                {
                  "type":"object",
                  "properties":{
                    "tenantId":{"type":"string"},
                    "customerPhone":{"type":"string"},
                    "customerName":{"type":"string"},
                    "categoryName":{"type":"string","description":"Categoria do servico. Preferir: Alvenaria, Hidraulica, Marcenaria, Montagem de Moveis, Serralheria, Eletronicos, Eletrodomesticos, Ar-Condicionado. Se nao encaixar, criar uma categoria especifica."},
                    "serviceTitle":{"type":"string"},
                    "startLocal":{"type":"string","description":"Data/hora local no formato 'yyyy-MM-dd HH:mm'"},
                    "durationMinutes":{"type":"integer"},
                    "address":{"type":"string"},
                    "notes":{"type":"string"},
                    "technicianName":{"type":"string"}
                  },
                  "required":["tenantId","customerPhone","customerName","categoryName","serviceTitle","startLocal","durationMinutes","address"]
                }
                """));

        var listBookingsTool = ChatTool.CreateFunctionTool(
            functionName: "list_bookings",
            functionDescription: "Lista agendamentos do telefone da conversa. Se nao houver periodo explicito, retorna todos.",
            functionParameters: BinaryData.FromString(
                """
                {
                  "type":"object",
                  "properties":{
                    "tenantId":{"type":"string"},
                    "customerPhone":{"type":["string","null"]},
                    "fromLocal":{"type":["string","null"],"description":"Data local 'yyyy-MM-dd' ou 'yyyy-MM-dd HH:mm'"},
                    "toLocal":{"type":["string","null"],"description":"Data local 'yyyy-MM-dd' ou 'yyyy-MM-dd HH:mm'"}
                  },
                  "required":["tenantId"]
                }
                """));

        var cancelBookingTool = ChatTool.CreateFunctionTool(
            functionName: "cancel_booking",
            functionDescription: "Cancela um agendamento por ID.",
            functionParameters: BinaryData.FromString(
                """
                {
                  "type":"object",
                  "properties":{
                    "tenantId":{"type":"string"},
                    "bookingId":{"type":"string"}
                  },
                  "required":["tenantId","bookingId"]
                }
                """));

        var rescheduleBookingTool = ChatTool.CreateFunctionTool(
            functionName: "reschedule_booking",
            functionDescription: "Altera data/hora de um agendamento existente.",
            functionParameters: BinaryData.FromString(
                """
                {
                  "type":"object",
                  "properties":{
                    "tenantId":{"type":"string"},
                    "bookingId":{"type":"string"},
                    "newStartLocal":{"type":"string","description":"Nova data/hora local no formato 'yyyy-MM-dd HH:mm'"}
                  },
                  "required":["tenantId","bookingId","newStartLocal"]
                }
                """));

        var lookupCepTool = ChatTool.CreateFunctionTool(
            functionName: "lookup_cep",
            functionDescription: "Consulta CEP no ViaCEP e retorna logradouro/bairro/localidade/uf.",
            functionParameters: BinaryData.FromString(
                """
                {
                  "type":"object",
                  "properties":{
                    "cep":{"type":"string","description":"CEP com ou sem hifen"}
                  },
                  "required":["cep"]
                }
                """));

        return new[] { createBookingTool, listBookingsTool, cancelBookingTool, rescheduleBookingTool, lookupCepTool };
    }

    private ToolResult ExecCreate(string argsJson, IncomingMessage incoming)
    {
        CreateBookingArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<CreateBookingArgs>(argsJson, JsonDefaults.Options);
        }
        catch
        {
            args = null;
        }

        if (args is null)
        {
            return BuildError("Argumentos invalidos para create_booking.");
        }

        if (string.IsNullOrWhiteSpace(args.ServiceTitle))
        {
            return BuildError("serviceTitle e obrigatorio.");
        }

        if (ServiceCategoryRules.IsDisallowedCategory(args.CategoryName))
        {
            args.CategoryName = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(args.CustomerName))
        {
            return BuildError("customerName e obrigatorio.");
        }

        if (string.IsNullOrWhiteSpace(args.Address))
        {
            return BuildError("address e obrigatorio.");
        }

        if (!TryParseLocalDateTime(args.StartLocal, out var startLocal))
        {
            return BuildError("startLocal invalido. Use yyyy-MM-dd HH:mm.");
        }

        if (args.DurationMinutes <= 0)
        {
            args.DurationMinutes = 60;
        }

        var chosenCategory = ServiceCategoryRules.ChooseCategory(
            candidate: args.CategoryName,
            serviceTitle: args.ServiceTitle,
            notes: args.Notes);
        var persistedCategory = _store.EnsureCategory(incoming.TenantId.Trim(), chosenCategory);

        var booking = _store.Create(
            tenantId: incoming.TenantId.Trim(),
            customerPhone: incoming.FromPhone.Trim(),
            customerName: args.CustomerName.Trim(),
            serviceCategory: persistedCategory.Name,
            serviceTitle: args.ServiceTitle.Trim(),
            startLocal: startLocal,
            durationMinutes: args.DurationMinutes,
            address: args.Address.Trim(),
            notes: args.Notes?.Trim() ?? string.Empty,
            technicianName: string.IsNullOrWhiteSpace(args.TechnicianName)
                ? "Tecnico disponivel"
                : args.TechnicianName.Trim());

        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            booking = new
            {
                booking.Id,
                booking.TenantId,
                booking.CustomerPhone,
                booking.CustomerName,
                booking.ServiceCategory,
                booking.ServiceTitle,
                startLocal = booking.StartLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                booking.DurationMinutes,
                booking.Address,
                booking.Notes,
                booking.TechnicianName
            }
        }, JsonDefaults.Options);

        return new ToolResult(payload);
    }

    private ToolResult ExecList(string argsJson, IncomingMessage incoming)
    {
        ListBookingsArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<ListBookingsArgs>(argsJson, JsonDefaults.Options);
        }
        catch
        {
            args = null;
        }

        if (args is null)
        {
            return BuildError("Argumentos invalidos para list_bookings.");
        }

        DateTime? from = null;
        DateTime? to = null;

        if (ShouldApplyDateFilters(incoming.Text))
        {
            if (!string.IsNullOrWhiteSpace(args.FromLocal))
            {
                if (!TryParseLocalDateOrDateTime(args.FromLocal!, out var parsedFrom))
                {
                    return BuildError("fromLocal invalido.");
                }

                from = parsedFrom;
            }

            if (!string.IsNullOrWhiteSpace(args.ToLocal))
            {
                if (!TryParseLocalDateOrDateTime(args.ToLocal!, out var parsedTo))
                {
                    return BuildError("toLocal invalido.");
                }

                to = parsedTo;
            }
        }

        var incomingPhone = incoming.FromPhone.Trim();
        var customerPhone = incomingPhone;

        var list = _store.List(
            tenantId: incoming.TenantId.Trim(),
            customerPhone: customerPhone,
            from: from,
            to: to);

        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            count = list.Count,
            bookings = list.Select(booking => new
            {
                booking.Id,
                booking.CustomerPhone,
                booking.CustomerName,
                booking.ServiceCategory,
                booking.ServiceTitle,
                startLocal = booking.StartLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                booking.DurationMinutes,
                booking.Address,
                booking.TechnicianName
            })
        }, JsonDefaults.Options);

        return new ToolResult(payload);
    }

    private ToolResult ExecCancel(string argsJson, IncomingMessage incoming)
    {
        CancelBookingArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<CancelBookingArgs>(argsJson, JsonDefaults.Options);
        }
        catch
        {
            args = null;
        }

        if (args is null || string.IsNullOrWhiteSpace(args.BookingId))
        {
            return BuildError("bookingId e obrigatorio.");
        }

        var success = _store.Cancel(incoming.TenantId.Trim(), args.BookingId.Trim());
        var payload = JsonSerializer.Serialize(new
        {
            ok = success,
            bookingId = args.BookingId.Trim()
        }, JsonDefaults.Options);

        return new ToolResult(payload);
    }

    private ToolResult ExecReschedule(string argsJson, IncomingMessage incoming)
    {
        RescheduleBookingArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<RescheduleBookingArgs>(argsJson, JsonDefaults.Options);
        }
        catch
        {
            args = null;
        }

        if (args is null || string.IsNullOrWhiteSpace(args.BookingId))
        {
            return BuildError("bookingId e obrigatorio.");
        }

        if (!TryParseLocalDateTime(args.NewStartLocal, out var newStartLocal))
        {
            return BuildError("newStartLocal invalido. Use yyyy-MM-dd HH:mm.");
        }

        var updated = _store.Reschedule(incoming.TenantId.Trim(), args.BookingId.Trim(), newStartLocal);
        if (updated is null)
        {
            return BuildError("Agendamento nao encontrado para alterar.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            booking = new
            {
                updated.Id,
                updated.CustomerPhone,
                updated.CustomerName,
                updated.ServiceCategory,
                updated.ServiceTitle,
                startLocal = updated.StartLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                updated.Address
            }
        }, JsonDefaults.Options);

        return new ToolResult(payload);
    }

    private async Task<ToolResult> ExecLookupCepAsync(string argsJson)
    {
        LookupCepArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<LookupCepArgs>(argsJson, JsonDefaults.Options);
        }
        catch
        {
            args = null;
        }

        var cepDigits = Regex.Replace(args?.Cep ?? string.Empty, @"\D", string.Empty);
        if (cepDigits.Length != 8)
        {
            return BuildError("CEP invalido. Envie 8 digitos.");
        }

        var url = $"https://viacep.com.br/ws/{cepDigits}/json/";

        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return BuildError($"Falha HTTP no ViaCEP: {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.TryGetProperty("erro", out var erroProp) &&
                erroProp.ValueKind == JsonValueKind.True)
            {
                return BuildError("CEP nao encontrado.");
            }

            string Get(string fieldName)
            {
                return doc.RootElement.TryGetProperty(fieldName, out var value) &&
                       value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
            }

            var payload = JsonSerializer.Serialize(new
            {
                ok = true,
                cep = Get("cep"),
                logradouro = Get("logradouro"),
                complemento = Get("complemento"),
                bairro = Get("bairro"),
                localidade = Get("localidade"),
                uf = Get("uf"),
                ibge = Get("ibge"),
                gia = Get("gia"),
                ddd = Get("ddd"),
                siafi = Get("siafi")
            }, JsonDefaults.Options);

            return new ToolResult(payload);
        }
        catch (Exception ex)
        {
            var payload = JsonSerializer.Serialize(new
            {
                ok = false,
                message = "Falha ao consultar CEP.",
                detail = ex.Message
            }, JsonDefaults.Options);

            return new ToolResult(payload);
        }
    }

    private static bool TryParseLocalDateTime(string input, out DateTime dateTime)
    {
        return DateTime.TryParseExact(
            input.Trim(),
            "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out dateTime);
    }

    private static bool TryParseLocalDateOrDateTime(string input, out DateTime dateTime)
    {
        var normalized = input.Trim();

        if (DateTime.TryParseExact(
                normalized,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out dateTime))
        {
            return true;
        }

        return DateTime.TryParseExact(
            normalized,
            "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out dateTime);
    }

    private static ToolResult BuildError(string message)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ok = false,
            message
        }, JsonDefaults.Options);

        return new ToolResult(payload);
    }

    private static bool ShouldApplyDateFilters(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        var normalized = userText.ToLowerInvariant();
        if (normalized.Contains("hoje", StringComparison.Ordinal) ||
            normalized.Contains("amanha", StringComparison.Ordinal) ||
            normalized.Contains("ontem", StringComparison.Ordinal) ||
            normalized.Contains("semana", StringComparison.Ordinal) ||
            normalized.Contains("mes", StringComparison.Ordinal) ||
            normalized.Contains("periodo", StringComparison.Ordinal) ||
            normalized.Contains("entre", StringComparison.Ordinal) ||
            normalized.Contains("de ", StringComparison.Ordinal) && normalized.Contains(" ate ", StringComparison.Ordinal))
        {
            return true;
        }

        return Regex.IsMatch(normalized, @"\b\d{1,2}[/-]\d{1,2}\b|\b\d{4}-\d{2}-\d{2}\b");
    }
}
