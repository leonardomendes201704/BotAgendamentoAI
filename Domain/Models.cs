using System.Text.Json.Serialization;

namespace BotAgendamentoAI.Domain;

public record IncomingMessage(
    string TenantId,
    string FromPhone,
    string Text
)
{
    public bool UserMessageAlreadyPersisted { get; init; }
    public IReadOnlyList<string> BatchedUserMessages { get; init; } = Array.Empty<string>();
}

public record Booking(
    string Id,
    string TenantId,
    string CustomerPhone,
    string CustomerName,
    string ServiceCategory,
    string ServiceTitle,
    DateTime StartLocal,
    int DurationMinutes,
    string Address,
    string Notes,
    string TechnicianName
);

public record ServiceCategory(
    long Id,
    string TenantId,
    string Name,
    string NormalizedName,
    DateTimeOffset CreatedAtUtc
);

public sealed record ToolResult(string ContentJson);

public sealed class ConversationMessage
{
    public long? Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class ConversationState
{
    public string TenantId { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SlotsJson { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class BotTextConfig
{
    public string TenantId { get; set; } = "A";
    public string MainMenuText { get; set; } =
        "1 - Agendar Servico\n2 - Consultar Agendamentos\n3 - Cancelar Agendamento\n4 - Alterar Agendamento\n5 - Falar com atendente\n6 - Encerrar atendimento";
    public string GreetingText { get; set; } = "Como posso ajudar voce hoje?";
    public string HumanHandoffText { get; set; } = "Vou te direcionar para um atendente humano.";
    public string ClosingText { get; set; } = "Atendimento encerrado. Envie MENU para iniciar novamente.";
    public string FallbackText { get; set; } = "Nao entendi. Escolha uma opcao do menu.";
    public int MessagePoolingSeconds { get; set; } = 15;

    public static BotTextConfig CreateDefault(string tenantId)
    {
        return new BotTextConfig
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim()
        };
    }
}

public sealed class ConversationSlots
{
    public string? ServiceTitle { get; set; }
    public string? ServiceCategory { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? DesiredDateTimeLocal { get; set; }
    public string? Cep { get; set; }
    public AddressSlots Address { get; set; } = new();
    public List<string> MissingFields { get; set; } = new();
    public string? LastBookingId { get; set; }
    public string? LastSeenAtUtc { get; set; }
    public string? Pending { get; set; }
    public string? MenuContext { get; set; }
    public List<BookingMenuOption> BookingOptions { get; set; } = new();
    public string? SelectedBookingId { get; set; }
    public string? SelectedBookingLabel { get; set; }
}

public sealed class AddressSlots
{
    public string? Logradouro { get; set; }
    public string? Bairro { get; set; }
    public string? Cidade { get; set; }
    public string? Uf { get; set; }
    public string? Numero { get; set; }
    public string? Complemento { get; set; }
}

public sealed class BookingMenuOption
{
    public int Number { get; set; }
    public string BookingId { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class PersistedToolCallsMetadata
{
    [JsonPropertyName("toolCalls")]
    public List<PersistedToolCall> ToolCalls { get; set; } = new();
}

public sealed class PersistedToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("functionName")]
    public string FunctionName { get; set; } = "";

    [JsonPropertyName("functionArgumentsJson")]
    public string FunctionArgumentsJson { get; set; } = "{}";
}

public sealed class CreateBookingArgs
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = "";

    [JsonPropertyName("customerPhone")]
    public string CustomerPhone { get; set; } = "";

    [JsonPropertyName("customerName")]
    public string CustomerName { get; set; } = "";

    [JsonPropertyName("serviceTitle")]
    public string ServiceTitle { get; set; } = "";

    [JsonPropertyName("categoryName")]
    public string CategoryName { get; set; } = "";

    [JsonPropertyName("startLocal")]
    public string StartLocal { get; set; } = "";

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; } = 60;

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    [JsonPropertyName("technicianName")]
    public string TechnicianName { get; set; } = "Tecnico disponivel";
}

public sealed class ListBookingsArgs
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = "";

    [JsonPropertyName("customerPhone")]
    public string? CustomerPhone { get; set; }

    [JsonPropertyName("fromLocal")]
    public string? FromLocal { get; set; }

    [JsonPropertyName("toLocal")]
    public string? ToLocal { get; set; }
}

public sealed class CancelBookingArgs
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = "";

    [JsonPropertyName("bookingId")]
    public string BookingId { get; set; } = "";
}

public sealed class LookupCepArgs
{
    [JsonPropertyName("cep")]
    public string Cep { get; set; } = "";
}

public sealed class RescheduleBookingArgs
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = "";

    [JsonPropertyName("bookingId")]
    public string BookingId { get; set; } = "";

    [JsonPropertyName("newStartLocal")]
    public string NewStartLocal { get; set; } = "";
}
