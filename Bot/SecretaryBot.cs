using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotAgendamentoAI.Data;
using BotAgendamentoAI.Domain;
using OpenAI.Chat;

namespace BotAgendamentoAI.Bot;

public sealed class SecretaryBot
{
    private const int MaxToolRounds = 8;
    private const int ShortMemoryLimit = 40;
    private const int HistoryFetchLimit = 160;

    private readonly ConversationRepository _repository;
    private readonly SecretaryTools _tools;
    private readonly StateManager _stateManager;
    private readonly TimeZoneInfo _timeZone;
    private readonly ChatClient _chat;

    private const string DefaultMainMenuBody = """
1 - Agendar Servico
2 - Consultar Agendamentos
3 - Cancelar Agendamento
4 - Alterar Agendamento
5 - Falar com atendente
6 - Encerrar atendimento
""";

    private const string SystemPrompt = """
Voce e uma secretaria virtual de agendamentos para atendimento por WhatsApp.
Responda em pt-BR com mensagens curtas, claras e educadas.

Objetivos:
- Entender se o cliente quer agendar, listar, cancelar ou alterar.
- Fazer perguntas objetivas apenas sobre campos faltantes.
- Usar ferramentas para criar/listar/cancelar/alterar e para consultar CEP.
- Confirmar no final: servico, data/hora, endereco e id do agendamento quando existir.

Regras:
- Use sempre o tenant e telefone da conversa atual.
- Nao invente dados de agendamento.
- Se receber CEP, chame lookup_cep e depois pergunte apenas numero/complemento se faltar.
- Antes de criar agendamento, confirme nome e telefone do cliente.
- Classifique todo agendamento em uma categoria especifica. Priorize: Alvenaria, Hidraulica, Marcenaria, Montagem de Moveis, Serralheria, Eletronicos, Eletrodomesticos, Ar-Condicionado.
- Nunca use categoria "Outros", "Geral" ou equivalente.
- Ao chamar create_booking, sempre preencha categoryName.
- Se o cliente pedir cancelamento/alteracao e nao tiver ID, ofereca lista numerada de agendamentos.
- Use o ConversationState/slots para evitar perguntas repetidas.
- Se houver retomada apos mais de 24h, confirme a intencao do cliente antes de assumir detalhes antigos.
- Quando interpretar hoje/amanha, use o bloco de data atual local no contexto.
""";

    public SecretaryBot(
        string apiKey,
        ConversationRepository repository,
        SecretaryTools tools,
        StateManager stateManager,
        TimeZoneInfo timeZone,
        string model = "gpt-4.1-mini")
    {
        _repository = repository;
        _tools = tools;
        _stateManager = stateManager;
        _timeZone = timeZone;
        _chat = new ChatClient(model: model, apiKey: apiKey);
    }

    public async Task<string> HandleAsync(IncomingMessage incoming)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, _timeZone);
        var botText = await _repository.GetBotTextConfig(incoming.TenantId);
        var userTextForState = GetUserTextForState(incoming);

        var state = await _repository.GetState(incoming.TenantId, incoming.FromPhone)
                    ?? CreateInitialState(incoming, nowUtc);
        var summary = string.IsNullOrWhiteSpace(state.Summary) ? "Sem contexto anterior." : state.Summary;
        var slots = _stateManager.ParseSlots(state.SlotsJson);
        slots.CustomerPhone = incoming.FromPhone.Trim();

        var rawHistory = await _repository.GetLast24h(incoming.TenantId, incoming.FromPhone, HistoryFetchLimit, nowUtc);
        var history = SanitizeAndTrimHistory(rawHistory, ShortMemoryLimit);
        var returningAfter24h = IsReturningAfter24h(slots.LastSeenAtUtc, rawHistory, nowUtc);

        slots = _stateManager.ApplyUserMessage(slots, userTextForState, nowUtc);

        if (!incoming.UserMessageAlreadyPersisted)
        {
            await _repository.AddMessage(new ConversationMessage
            {
                TenantId = incoming.TenantId,
                Phone = incoming.FromPhone,
                Direction = "in",
                Role = "user",
                Content = incoming.Text,
                CreatedAtUtc = nowUtc
            });
        }

        var menuHandled = await TryHandleMenuFlowAsync(
            incoming,
            slots,
            rawHistory.Count == 0,
            nowUtc,
            botText);

        if (menuHandled.Handled)
        {
            await PersistAssistantOutput(incoming, menuHandled.Response, null);
            summary = _stateManager.BuildSummary(summary, slots, userTextForState, menuHandled.Response);
            await PersistState(incoming, slots, summary);
            return menuHandled.Response;
        }

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new SystemChatMessage(BuildDateTimeContext(nowLocal)),
            new SystemChatMessage(BuildCategoryContext(incoming.TenantId)),
            new SystemChatMessage(BuildBotMessageContext(botText)),
            new SystemChatMessage(_stateManager.BuildStatePromptBlock(summary, slots, returningAfter24h))
        };

        foreach (var item in history)
        {
            TryAddHistoryMessage(messages, item);
        }

        if (!incoming.UserMessageAlreadyPersisted)
        {
            messages.Add(new UserChatMessage(incoming.Text));
        }

        var options = BuildOptions();

        for (var step = 0; step < MaxToolRounds; step++)
        {
            ChatCompletion completion;
            try
            {
                completion = await _chat.CompleteChatAsync(messages, options);
            }
            catch (Exception ex)
            {
                var failure = "Desculpe, houve um erro temporario. Pode tentar novamente em instantes?";
                await PersistAssistantOutput(incoming, failure, null);
                await PersistState(incoming, slots, _stateManager.BuildSummary(summary, slots, userTextForState, failure));
                Console.WriteLine($"[erro-openai] {ex.Message}");
                return failure;
            }

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                messages.Add(new AssistantChatMessage(completion));

                var toolCallMetadata = BuildToolCallMetadata(completion.ToolCalls);
                var assistantToolCallText = ExtractAssistantText(completion);
                await PersistAssistantOutput(incoming, assistantToolCallText, toolCallMetadata);

                foreach (var toolCall in completion.ToolCalls)
                {
                    var argsJson = toolCall.FunctionArguments?.ToString() ?? "{}";
                    Console.WriteLine($"[tool] {toolCall.FunctionName} ({incoming.TenantId}/{incoming.FromPhone})");
                    Console.WriteLine($"[tool-args] {argsJson}");

                    var toolResult = await _tools.ExecuteAsync(toolCall.FunctionName, argsJson, incoming);
                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult.ContentJson));

                    await _repository.AddMessage(new ConversationMessage
                    {
                        TenantId = incoming.TenantId,
                        Phone = incoming.FromPhone,
                        Direction = "out",
                        Role = "tool",
                        Content = toolResult.ContentJson,
                        ToolName = toolCall.FunctionName,
                        ToolCallId = toolCall.Id,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        MetadataJson = JsonSerializer.Serialize(new
                        {
                            functionName = toolCall.FunctionName,
                            functionArguments = argsJson
                        }, JsonDefaults.Options)
                    });

                    slots = _stateManager.ApplyToolResult(slots, toolCall.FunctionName, toolResult.ContentJson, DateTimeOffset.UtcNow);
                }

                continue;
            }

            messages.Add(new AssistantChatMessage(completion));
            var assistantText = ExtractAssistantText(completion);
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                assistantText = "Perfeito. Pode me confirmar servico, data/hora e endereco completo?";
            }

            await PersistAssistantOutput(incoming, assistantText, null);

            summary = _stateManager.BuildSummary(summary, slots, userTextForState, assistantText);
            await PersistState(incoming, slots, summary);
            return assistantText;
        }

        var fallback = "Desculpe, nao consegui concluir agora. Pode repetir o pedido com servico, data/hora e endereco?";
        await PersistAssistantOutput(incoming, fallback, null);
        await PersistState(incoming, slots, _stateManager.BuildSummary(summary, slots, userTextForState, fallback));
        return fallback;
    }

    private async Task<(bool Handled, string Response)> TryHandleMenuFlowAsync(
        IncomingMessage incoming,
        ConversationSlots slots,
        bool isNewConversation,
        DateTimeOffset nowUtc,
        BotTextConfig botText)
    {
        var normalized = NormalizeText(incoming.Text);
        var menuContext = (slots.MenuContext ?? string.Empty).Trim().ToLowerInvariant();

        if (menuContext == "closed")
        {
            if (IsMenuIntent(normalized))
            {
                slots.MenuContext = "awaiting_menu_choice";
                slots.Pending = null;
                ClearBookingSelection(slots);
                return (true, BuildMainMenuResponse(botText, includeGreeting: false));
            }

            return (true, $"{ResolveClosingText(botText)}\nEnvie MENU para iniciar novamente.");
        }

        if (menuContext == "human_handoff")
        {
            if (IsMenuIntent(normalized))
            {
                slots.MenuContext = "awaiting_menu_choice";
                slots.Pending = null;
                ClearBookingSelection(slots);
                return (true, BuildMainMenuResponse(botText, includeGreeting: false));
            }

            return (true, $"{ResolveHumanHandoffText(botText)}\nEnvie MENU para voltar ao bot.");
        }

        if (menuContext == "awaiting_cancel_selection")
        {
            return await HandleCancelSelectionAsync(incoming, slots, normalized, nowUtc, botText);
        }

        if (menuContext == "awaiting_reschedule_selection")
        {
            return HandleRescheduleSelection(slots, normalized, botText);
        }

        if (menuContext == "awaiting_reschedule_datetime")
        {
            return await HandleRescheduleDateTimeAsync(incoming, slots, normalized, nowUtc, botText);
        }

        if (menuContext == "awaiting_menu_choice")
        {
            return await HandleMenuChoiceAsync(incoming, slots, normalized, nowUtc, botText);
        }

        if (ShouldShowMainMenu(normalized, isNewConversation))
        {
            slots.MenuContext = "awaiting_menu_choice";
            slots.Pending = null;
            ClearBookingSelection(slots);
            return (true, BuildMainMenuResponse(botText, includeGreeting: true));
        }

        var directNumericChoice = ParseNumberChoice(normalized, 1, 6);
        if (directNumericChoice.HasValue)
        {
            slots.MenuContext = "awaiting_menu_choice";
            return await HandleMenuChoiceAsync(incoming, slots, normalized, nowUtc, botText);
        }

        var directIntentChoice = ParseMenuChoice(normalized, allowKeywordMapping: true);
        if (directIntentChoice.HasValue &&
            !IsGreetingIntent(normalized) &&
            !IsMenuIntent(normalized))
        {
            slots.MenuContext = "awaiting_menu_choice";
            return await HandleMenuChoiceAsync(incoming, slots, normalized, nowUtc, botText);
        }

        return (false, string.Empty);
    }

    private async Task<(bool Handled, string Response)> HandleMenuChoiceAsync(
        IncomingMessage incoming,
        ConversationSlots slots,
        string normalizedText,
        DateTimeOffset nowUtc,
        BotTextConfig botText)
    {
        if (IsMenuIntent(normalizedText) || IsGreetingIntent(normalizedText))
        {
            return (true, BuildMainMenuResponse(botText, includeGreeting: false));
        }

        var choice = ParseMenuChoice(normalizedText, allowKeywordMapping: true);
        if (!choice.HasValue)
        {
            return (true, $"{ResolveFallbackText(botText)}\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
        }

        switch (choice.Value)
        {
            case 1:
                slots.MenuContext = "create_flow";
                slots.Pending = "create_booking";
                ClearBookingSelection(slots);
                return (true, "Perfeito! Vamos agendar.\nMe diga qual servico voce precisa e a data/hora desejada.");

            case 2:
            {
                var bookings = _tools.ListForCustomer(incoming.TenantId, incoming.FromPhone);
                var listToolResultJson = BuildListToolResultPayload(bookings);
                await PersistSyntheticToolExecutionAsync(
                    incoming,
                    "list_bookings",
                    JsonSerializer.Serialize(new { tenantId = incoming.TenantId, customerPhone = incoming.FromPhone }, JsonDefaults.Options),
                    listToolResultJson);
                slots = _stateManager.ApplyToolResult(slots, "list_bookings", listToolResultJson, nowUtc);

                if (bookings.Count == 0)
                {
                    slots.MenuContext = "awaiting_menu_choice";
                    slots.Pending = "bookings_listed";
                    ClearBookingSelection(slots);
                    return (true, $"Voce nao possui agendamentos no momento.\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
                }

                var options = BuildBookingOptions(bookings);
                slots.BookingOptions = options;
                slots.MenuContext = "awaiting_menu_choice";
                slots.Pending = "bookings_listed";
                slots.SelectedBookingId = null;
                slots.SelectedBookingLabel = null;

                var response = BuildBookingListResponse(
                    "Estes sao seus agendamentos:",
                    options,
                    "Digite 3 para cancelar, 4 para alterar, 1 para novo agendamento ou 6 para encerrar.");
                return (true, response);
            }

            case 3:
            {
                var bookings = _tools.ListForCustomer(incoming.TenantId, incoming.FromPhone);
                var listToolResultJson = BuildListToolResultPayload(bookings);
                await PersistSyntheticToolExecutionAsync(
                    incoming,
                    "list_bookings",
                    JsonSerializer.Serialize(new { tenantId = incoming.TenantId, customerPhone = incoming.FromPhone }, JsonDefaults.Options),
                    listToolResultJson);
                slots = _stateManager.ApplyToolResult(slots, "list_bookings", listToolResultJson, nowUtc);

                if (bookings.Count == 0)
                {
                    slots.MenuContext = "awaiting_menu_choice";
                    slots.Pending = "bookings_listed";
                    ClearBookingSelection(slots);
                    return (true, $"Voce nao possui agendamentos para cancelar.\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
                }

                var options = BuildBookingOptions(bookings);
                slots.BookingOptions = options;
                slots.MenuContext = "awaiting_cancel_selection";
                slots.Pending = "cancel_booking";
                slots.SelectedBookingId = null;
                slots.SelectedBookingLabel = null;

                return (true, BuildBookingListResponse(
                    "Escolha o numero do agendamento que deseja cancelar:",
                    options,
                    "Exemplo: 1"));
            }

            case 4:
            {
                var bookings = _tools.ListForCustomer(incoming.TenantId, incoming.FromPhone);
                var listToolResultJson = BuildListToolResultPayload(bookings);
                await PersistSyntheticToolExecutionAsync(
                    incoming,
                    "list_bookings",
                    JsonSerializer.Serialize(new { tenantId = incoming.TenantId, customerPhone = incoming.FromPhone }, JsonDefaults.Options),
                    listToolResultJson);
                slots = _stateManager.ApplyToolResult(slots, "list_bookings", listToolResultJson, nowUtc);

                if (bookings.Count == 0)
                {
                    slots.MenuContext = "awaiting_menu_choice";
                    slots.Pending = "bookings_listed";
                    ClearBookingSelection(slots);
                    return (true, $"Voce nao possui agendamentos para alterar.\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
                }

                var options = BuildBookingOptions(bookings);
                slots.BookingOptions = options;
                slots.MenuContext = "awaiting_reschedule_selection";
                slots.Pending = "reschedule_booking";
                slots.SelectedBookingId = null;
                slots.SelectedBookingLabel = null;

                return (true, BuildBookingListResponse(
                    "Escolha o numero do agendamento que deseja alterar:",
                    options,
                    "Exemplo: 2"));
            }

            case 5:
                slots.MenuContext = "human_handoff";
                slots.Pending = "human_handoff";
                ClearBookingSelection(slots);
                return (true, $"{ResolveHumanHandoffText(botText)}\nSe quiser voltar ao bot, envie MENU.");

            case 6:
                slots.MenuContext = "closed";
                slots.Pending = "closed";
                ClearBookingSelection(slots);
                return (true, ResolveClosingText(botText));

            default:
                return (true, $"{ResolveFallbackText(botText)}\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
        }
    }

    private async Task<(bool Handled, string Response)> HandleCancelSelectionAsync(
        IncomingMessage incoming,
        ConversationSlots slots,
        string normalizedText,
        DateTimeOffset nowUtc,
        BotTextConfig botText)
    {
        if (IsMenuIntent(normalizedText))
        {
            slots.MenuContext = "awaiting_menu_choice";
            slots.Pending = null;
            ClearBookingSelection(slots);
            return (true, BuildMainMenuResponse(botText, includeGreeting: false));
        }

        var number = ParseNumberChoice(normalizedText, 1, 99);
        if (!number.HasValue || slots.BookingOptions.Count == 0)
        {
            return (true, BuildBookingListResponse(
                "Nao entendi. Envie apenas o numero do agendamento para cancelar:",
                slots.BookingOptions,
                "Exemplo: 1"));
        }

        var selected = slots.BookingOptions.FirstOrDefault(o => o.Number == number.Value);
        if (selected is null)
        {
            return (true, BuildBookingListResponse(
                "Opcao invalida. Escolha um numero da lista:",
                slots.BookingOptions,
                "Exemplo: 2"));
        }

        var success = _tools.CancelById(incoming.TenantId, selected.BookingId);
        var toolResultJson = JsonSerializer.Serialize(new
        {
            ok = success,
            bookingId = selected.BookingId
        }, JsonDefaults.Options);

        await PersistSyntheticToolExecutionAsync(
            incoming,
            "cancel_booking",
            JsonSerializer.Serialize(new { tenantId = incoming.TenantId, bookingId = selected.BookingId }, JsonDefaults.Options),
            toolResultJson);

        slots = _stateManager.ApplyToolResult(slots, "cancel_booking", toolResultJson, nowUtc);
        slots.LastBookingId = selected.BookingId;
        slots.MenuContext = "awaiting_menu_choice";
        ClearBookingSelection(slots);

        if (!success)
        {
            return (true, $"Nao consegui cancelar o agendamento selecionado.\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
        }

        return (true, $"Agendamento cancelado com sucesso:\n{selected.Label}\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
    }

    private (bool Handled, string Response) HandleRescheduleSelection(
        ConversationSlots slots,
        string normalizedText,
        BotTextConfig botText)
    {
        if (IsMenuIntent(normalizedText))
        {
            slots.MenuContext = "awaiting_menu_choice";
            slots.Pending = null;
            ClearBookingSelection(slots);
            return (true, BuildMainMenuResponse(botText, includeGreeting: false));
        }

        var number = ParseNumberChoice(normalizedText, 1, 99);
        if (!number.HasValue || slots.BookingOptions.Count == 0)
        {
            return (true, BuildBookingListResponse(
                "Nao entendi. Envie apenas o numero do agendamento para alterar:",
                slots.BookingOptions,
                "Exemplo: 1"));
        }

        var selected = slots.BookingOptions.FirstOrDefault(o => o.Number == number.Value);
        if (selected is null)
        {
            return (true, BuildBookingListResponse(
                "Opcao invalida. Escolha um numero da lista:",
                slots.BookingOptions,
                "Exemplo: 2"));
        }

        slots.SelectedBookingId = selected.BookingId;
        slots.SelectedBookingLabel = selected.Label;
        slots.MenuContext = "awaiting_reschedule_datetime";
        slots.Pending = "reschedule_booking";

        return (true,
            $"Voce escolheu:\n{selected.Label}\n\nInforme a nova data e hora (ex: 07/03/2026 15:30 ou amanha 14:00).");
    }

    private async Task<(bool Handled, string Response)> HandleRescheduleDateTimeAsync(
        IncomingMessage incoming,
        ConversationSlots slots,
        string normalizedText,
        DateTimeOffset nowUtc,
        BotTextConfig botText)
    {
        if (IsMenuIntent(normalizedText))
        {
            slots.MenuContext = "awaiting_menu_choice";
            slots.Pending = null;
            ClearBookingSelection(slots);
            return (true, BuildMainMenuResponse(botText, includeGreeting: false));
        }

        if (string.IsNullOrWhiteSpace(slots.SelectedBookingId))
        {
            slots.MenuContext = "awaiting_menu_choice";
            ClearBookingSelection(slots);
            return (true, $"Nao encontrei o agendamento selecionado.\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
        }

        if (!_stateManager.TryParseDateTimeFromMessage(incoming.Text, nowUtc, out var newStartLocal))
        {
            return (true, "Nao consegui entender a nova data/hora. Envie no formato dd/MM/yyyy HH:mm (ex: 07/03/2026 15:30).");
        }

        var updated = _tools.RescheduleById(incoming.TenantId, slots.SelectedBookingId, newStartLocal);
        var toolResultJson = updated is null
            ? JsonSerializer.Serialize(new { ok = false, message = "Agendamento nao encontrado para alterar." }, JsonDefaults.Options)
            : JsonSerializer.Serialize(new
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

        await PersistSyntheticToolExecutionAsync(
            incoming,
            "reschedule_booking",
            JsonSerializer.Serialize(new
            {
                tenantId = incoming.TenantId,
                bookingId = slots.SelectedBookingId,
                newStartLocal = newStartLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            }, JsonDefaults.Options),
            toolResultJson);

        slots = _stateManager.ApplyToolResult(slots, "reschedule_booking", toolResultJson, nowUtc);
        slots.LastBookingId = slots.SelectedBookingId;
        slots.DesiredDateTimeLocal = newStartLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        slots.MenuContext = "awaiting_menu_choice";
        slots.Pending = "booking_rescheduled";

        var selectedLabel = slots.SelectedBookingLabel ?? "agendamento selecionado";
        ClearBookingSelection(slots);

        if (updated is null)
        {
            return (true, $"Nao consegui alterar o {selectedLabel}.\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
        }

        return (true,
            $"Agendamento alterado com sucesso para {updated.StartLocal:dd/MM/yyyy 'as' HH:mm}.\nCategoria: {updated.ServiceCategory}\nServico: {updated.ServiceTitle}\nEndereco: {updated.Address}\n\n{BuildMainMenuResponse(botText, includeGreeting: false)}");
    }

    private ChatCompletionOptions BuildOptions()
    {
        var options = new ChatCompletionOptions
        {
            Temperature = 0.2f
        };

        foreach (var tool in _tools.GetToolDefinitions())
        {
            options.Tools.Add(tool);
        }

        return options;
    }

    private async Task PersistAssistantOutput(IncomingMessage incoming, string content, string? metadataJson)
    {
        await _repository.AddMessage(new ConversationMessage
        {
            TenantId = incoming.TenantId,
            Phone = incoming.FromPhone,
            Direction = "out",
            Role = "assistant",
            Content = content ?? string.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MetadataJson = metadataJson
        });
    }

    private async Task PersistSyntheticToolExecutionAsync(
        IncomingMessage incoming,
        string toolName,
        string argsJson,
        string toolResultJson)
    {
        var toolCallId = $"manual_{Guid.NewGuid():N}";
        var assistantMetadata = JsonSerializer.Serialize(new PersistedToolCallsMetadata
        {
            ToolCalls = new List<PersistedToolCall>
            {
                new()
                {
                    Id = toolCallId,
                    FunctionName = toolName,
                    FunctionArgumentsJson = argsJson
                }
            }
        }, JsonDefaults.Options);

        await PersistAssistantOutput(incoming, string.Empty, assistantMetadata);

        await _repository.AddMessage(new ConversationMessage
        {
            TenantId = incoming.TenantId,
            Phone = incoming.FromPhone,
            Direction = "out",
            Role = "tool",
            Content = toolResultJson,
            ToolName = toolName,
            ToolCallId = toolCallId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MetadataJson = JsonSerializer.Serialize(new
            {
                functionName = toolName,
                functionArguments = argsJson
            }, JsonDefaults.Options)
        });
    }

    private async Task PersistState(IncomingMessage incoming, ConversationSlots slots, string summary)
    {
        await _repository.UpsertState(new ConversationState
        {
            TenantId = incoming.TenantId,
            Phone = incoming.FromPhone,
            Summary = summary,
            SlotsJson = _stateManager.SerializeSlots(slots),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static ConversationState CreateInitialState(IncomingMessage incoming, DateTimeOffset nowUtc)
    {
        var slots = new ConversationSlots
        {
            LastSeenAtUtc = nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };

        return new ConversationState
        {
            TenantId = incoming.TenantId,
            Phone = incoming.FromPhone,
            Summary = "Sem contexto anterior.",
            SlotsJson = JsonSerializer.Serialize(slots, JsonDefaults.Options),
            UpdatedAtUtc = nowUtc
        };
    }

    private string BuildDateTimeContext(DateTimeOffset nowLocal)
    {
        return $"""
Data atual local: {nowLocal:yyyy-MM-dd HH:mm:ss}
Timezone local: {_timeZone.Id} ({nowLocal.Offset})
Referencia para interpretar hoje/amanha: use a data local acima.
""";
    }

    private string BuildCategoryContext(string tenantId)
    {
        var categories = _tools.GetCategoryNames(tenantId);
        if (categories.Count == 0)
        {
            categories = ServiceCategoryRules.PreferredCategories.ToList();
        }

        return $"""
Categorias disponiveis para classificacao:
- {string.Join("\n- ", categories)}

Regra de classificacao:
- Ao criar agendamento, defina categoryName.
- Se nao houver categoria adequada, crie uma categoria especifica (nunca "Outros").
""";
    }

    private static string BuildBotMessageContext(BotTextConfig botText)
    {
        return $"""
Tenant message config:
- greetingText: {ResolveGreetingText(botText)}
- humanHandoffText: {ResolveHumanHandoffText(botText)}
- closingText: {ResolveClosingText(botText)}
- fallbackText: {ResolveFallbackText(botText)}

When menu flow is active, keep language aligned to these texts.
""";
    }

    private static string BuildMainMenuResponse(BotTextConfig botText, bool includeGreeting)
    {
        var menuBody = ResolveMainMenuBody(botText);
        var menuText =
            menuBody.Contains("como posso ajudar", StringComparison.OrdinalIgnoreCase) ||
            menuBody.Contains("escolha uma opcao", StringComparison.OrdinalIgnoreCase)
                ? menuBody
                : $"Como posso ajudar? Escolha uma opcao:\n{menuBody}";

        if (!includeGreeting)
        {
            return menuText;
        }

        var greeting = ResolveGreetingText(botText);
        if (string.IsNullOrWhiteSpace(greeting))
        {
            return menuText;
        }

        if (greeting.Contains("como posso ajudar", StringComparison.OrdinalIgnoreCase) &&
            menuText.Contains("como posso ajudar", StringComparison.OrdinalIgnoreCase))
        {
            return menuText;
        }

        return $"{greeting}\n\n{menuText}";
    }

    private static string ResolveMainMenuBody(BotTextConfig botText)
    {
        return string.IsNullOrWhiteSpace(botText.MainMenuText)
            ? DefaultMainMenuBody
            : botText.MainMenuText.Trim();
    }

    private static string ResolveGreetingText(BotTextConfig botText)
    {
        return string.IsNullOrWhiteSpace(botText.GreetingText)
            ? "Como posso ajudar voce hoje?"
            : botText.GreetingText.Trim();
    }

    private static string ResolveHumanHandoffText(BotTextConfig botText)
    {
        return string.IsNullOrWhiteSpace(botText.HumanHandoffText)
            ? "Vou te direcionar para um atendente humano."
            : botText.HumanHandoffText.Trim();
    }

    private static string ResolveClosingText(BotTextConfig botText)
    {
        return string.IsNullOrWhiteSpace(botText.ClosingText)
            ? "Atendimento encerrado. Envie MENU para iniciar novamente."
            : botText.ClosingText.Trim();
    }

    private static string ResolveFallbackText(BotTextConfig botText)
    {
        return string.IsNullOrWhiteSpace(botText.FallbackText)
            ? "Nao entendi. Escolha uma opcao do menu."
            : botText.FallbackText.Trim();
    }

    private static string ExtractAssistantText(ChatCompletion completion)
    {
        if (completion.Content.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            completion.Content
                .Select(part => part.Text?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string BuildToolCallMetadata(IReadOnlyList<ChatToolCall> toolCalls)
    {
        var metadata = new PersistedToolCallsMetadata
        {
            ToolCalls = toolCalls
                .Select(tc => new PersistedToolCall
                {
                    Id = tc.Id,
                    FunctionName = tc.FunctionName,
                    FunctionArgumentsJson = tc.FunctionArguments?.ToString() ?? "{}"
                })
                .ToList()
        };

        return JsonSerializer.Serialize(metadata, JsonDefaults.Options);
    }

    private void TryAddHistoryMessage(List<ChatMessage> messages, ConversationMessage item)
    {
        switch (item.Role)
        {
            case "user":
                messages.Add(new UserChatMessage(item.Content));
                break;

            case "assistant":
            {
                var toolCalls = ParseToolCalls(item.MetadataJson);
                if (toolCalls.Count > 0)
                {
                    messages.Add(new AssistantChatMessage(toolCalls));
                }
                else
                {
                    messages.Add(new AssistantChatMessage(item.Content));
                }

                break;
            }

            case "tool":
                if (!string.IsNullOrWhiteSpace(item.ToolCallId))
                {
                    messages.Add(new ToolChatMessage(item.ToolCallId, item.Content));
                }

                break;

            case "system":
                messages.Add(new SystemChatMessage(item.Content));
                break;
        }
    }

    private IReadOnlyList<ChatToolCall> ParseToolCalls(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return Array.Empty<ChatToolCall>();
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<PersistedToolCallsMetadata>(metadataJson, JsonDefaults.Options);
            if (metadata?.ToolCalls is null || metadata.ToolCalls.Count == 0)
            {
                return Array.Empty<ChatToolCall>();
            }

            var calls = new List<ChatToolCall>();
            foreach (var persisted in metadata.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(persisted.Id) || string.IsNullOrWhiteSpace(persisted.FunctionName))
                {
                    continue;
                }

                var args = string.IsNullOrWhiteSpace(persisted.FunctionArgumentsJson)
                    ? "{}"
                    : persisted.FunctionArgumentsJson;

                calls.Add(ChatToolCall.CreateFunctionToolCall(
                    persisted.Id,
                    persisted.FunctionName,
                    BinaryData.FromString(args)));
            }

            return calls;
        }
        catch
        {
            return Array.Empty<ChatToolCall>();
        }
    }

    private static bool IsReturningAfter24h(
        string? lastSeenAtUtc,
        IReadOnlyList<ConversationMessage> rawHistory,
        DateTimeOffset nowUtc)
    {
        if (rawHistory.Count > 0 || string.IsNullOrWhiteSpace(lastSeenAtUtc))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                lastSeenAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        return nowUtc - parsed.ToUniversalTime() > TimeSpan.FromHours(24);
    }

    private IReadOnlyList<ConversationMessage> SanitizeAndTrimHistory(
        IReadOnlyList<ConversationMessage> rawHistory,
        int maxMessages)
    {
        var firstPass = SanitizeHistorySequence(rawHistory);
        if (firstPass.Count <= maxMessages)
        {
            return firstPass;
        }

        var trimmed = firstPass.Skip(firstPass.Count - maxMessages).ToList();
        return SanitizeHistorySequence(trimmed);
    }

    private IReadOnlyList<ConversationMessage> SanitizeHistorySequence(IReadOnlyList<ConversationMessage> source)
    {
        var output = new List<ConversationMessage>();

        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];

            if (item.Role == "assistant")
            {
                var toolCallIds = ParseToolCallIds(item.MetadataJson);
                if (toolCallIds.Count == 0)
                {
                    output.Add(item);
                    continue;
                }

                var pending = new HashSet<string>(toolCallIds, StringComparer.Ordinal);
                var toolBlock = new List<ConversationMessage>();
                var j = i + 1;

                while (j < source.Count && pending.Count > 0)
                {
                    var next = source[j];
                    if (next.Role != "tool" || string.IsNullOrWhiteSpace(next.ToolCallId))
                    {
                        toolBlock.Clear();
                        break;
                    }

                    if (!pending.Remove(next.ToolCallId))
                    {
                        toolBlock.Clear();
                        break;
                    }

                    toolBlock.Add(next);
                    j++;
                }

                if (pending.Count > 0 || toolBlock.Count == 0)
                {
                    continue;
                }

                output.Add(item);
                output.AddRange(toolBlock);
                i = j - 1;
                continue;
            }

            if (item.Role == "tool")
            {
                continue;
            }

            if (item.Role is "user" or "system")
            {
                output.Add(item);
            }
        }

        return output;
    }

    private static IReadOnlyList<string> ParseToolCallIds(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<PersistedToolCallsMetadata>(metadataJson, JsonDefaults.Options);
            if (metadata?.ToolCalls is null)
            {
                return Array.Empty<string>();
            }

            return metadata.ToolCalls
                .Select(tc => tc.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static List<BookingMenuOption> BuildBookingOptions(IReadOnlyList<Booking> bookings)
    {
        var options = new List<BookingMenuOption>(bookings.Count);
        for (var i = 0; i < bookings.Count; i++)
        {
            var booking = bookings[i];
            options.Add(new BookingMenuOption
            {
                Number = i + 1,
                BookingId = booking.Id,
                Label = $"{booking.ServiceCategory} | {booking.ServiceTitle} - {booking.StartLocal:dd/MM/yyyy 'as' HH:mm} - {booking.Address}"
            });
        }

        return options;
    }

    private static string BuildBookingListResponse(string title, IReadOnlyList<BookingMenuOption> options, string footer)
    {
        if (options.Count == 0)
        {
            return title;
        }

        var lines = new List<string> { title };
        lines.AddRange(options.Select(option => $"{option.Number} - {option.Label}"));
        if (!string.IsNullOrWhiteSpace(footer))
        {
            lines.Add(string.Empty);
            lines.Add(footer);
        }

        return string.Join("\n", lines);
    }

    private static string BuildListToolResultPayload(IReadOnlyList<Booking> bookings)
    {
        return JsonSerializer.Serialize(new
        {
            ok = true,
            count = bookings.Count,
            bookings = bookings.Select(booking => new
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
    }

    private static bool ShouldShowMainMenu(string normalizedText, bool isNewConversation)
    {
        if (IsMenuIntent(normalizedText))
        {
            return true;
        }

        if (IsGreetingIntent(normalizedText))
        {
            return true;
        }

        return isNewConversation && normalizedText.Length <= 2;
    }

    private static int? ParseMenuChoice(string normalizedText, bool allowKeywordMapping)
    {
        var numeric = ParseNumberChoice(normalizedText, 1, 6);
        if (numeric.HasValue)
        {
            return numeric;
        }

        if (!allowKeywordMapping)
        {
            return null;
        }

        if (normalizedText.Contains("agendar", StringComparison.Ordinal) ||
            normalizedText.Contains("novo", StringComparison.Ordinal))
        {
            return 1;
        }

        if (normalizedText.Contains("consultar", StringComparison.Ordinal) ||
            normalizedText.Contains("listar", StringComparison.Ordinal) ||
            normalizedText.Contains("ver agendamento", StringComparison.Ordinal) ||
            normalizedText.Contains("meus agendamentos", StringComparison.Ordinal) ||
            normalizedText.Contains("quais", StringComparison.Ordinal) ||
            normalizedText.Contains("agendamentos", StringComparison.Ordinal))
        {
            return 2;
        }

        if (normalizedText.Contains("cancelar", StringComparison.Ordinal))
        {
            return 3;
        }

        if (normalizedText.Contains("alterar", StringComparison.Ordinal) ||
            normalizedText.Contains("remarcar", StringComparison.Ordinal) ||
            normalizedText.Contains("reagendar", StringComparison.Ordinal))
        {
            return 4;
        }

        if (normalizedText.Contains("atendente", StringComparison.Ordinal) ||
            normalizedText.Contains("humano", StringComparison.Ordinal))
        {
            return 5;
        }

        if (normalizedText.Contains("encerrar", StringComparison.Ordinal) ||
            normalizedText.Contains("finalizar", StringComparison.Ordinal) ||
            normalizedText.Contains("sair", StringComparison.Ordinal))
        {
            return 6;
        }

        return null;
    }

    private static int? ParseNumberChoice(string normalizedText, int min, int max)
    {
        var match = Regex.Match(normalizedText, @"\b(\d{1,2})\b");
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        if (value < min || value > max)
        {
            return null;
        }

        return value;
    }

    private static bool IsGreetingIntent(string normalizedText)
    {
        if (normalizedText == "oi" || normalizedText == "ola")
        {
            return true;
        }

        return normalizedText.StartsWith("bom dia", StringComparison.Ordinal) ||
               normalizedText.StartsWith("boa tarde", StringComparison.Ordinal) ||
               normalizedText.StartsWith("boa noite", StringComparison.Ordinal);
    }

    private static bool IsMenuIntent(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        return normalizedText == "menu" ||
               normalizedText == "inicio" ||
               normalizedText == "iniciar" ||
               normalizedText == "comecar" ||
               normalizedText == "voltar";
    }

    private static string NormalizeText(string input)
    {
        var lowered = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lowered.Length);

        foreach (var c in lowered)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).Trim();
    }

    private static void ClearBookingSelection(ConversationSlots slots)
    {
        slots.BookingOptions = new List<BookingMenuOption>();
        slots.SelectedBookingId = null;
        slots.SelectedBookingLabel = null;
    }

    private static string GetUserTextForState(IncomingMessage incoming)
    {
        if (incoming.BatchedUserMessages.Count == 0)
        {
            return incoming.Text;
        }

        return string.Join("\n",
            incoming.BatchedUserMessages
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message.Trim()));
    }
}
