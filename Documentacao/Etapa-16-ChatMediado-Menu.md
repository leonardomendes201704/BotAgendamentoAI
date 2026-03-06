# Etapa 16 - Consulta de agendamentos travando por sessao de chat mediado

## Data
- 2026-03-06

## Problema
- Ao pedir "Meus agendamentos" (ex.: opcao `2`), o bot nao respondia.

## Causa raiz
- Usuario com sessao em `CHAT_MEDIATED` tinha mensagens interceptadas por `ChatMediatorService`.
- Entrada de menu (`2`, `menu`, botoes de menu) era tratada como chat e nao seguia para o fluxo de cliente.

## Correcao
- Arquivo: `BotAgendamentoAI.Telegram/Features/Shared/ChatMediatorService.cs`
  - Adicionada deteccao `ShouldReleaseToMenu(...)`.
  - Quando usuario envia comando/menu durante chat mediado:
    - bot encerra modo chat no estado de sessao;
    - salva sessao;
    - retorna `false` para deixar o orquestrador processar mensagem no fluxo normal.

## Cobertura da liberacao para menu
- `/menu`, `menu`, `Voltar`, `Cancelar`
- numericos `1..6`
- textos de itens de menu cliente/prestador

## Validacao
- `dotnet build BotAgendamentoAI.Telegram/...` ?
- `dotnet test BotAgendamentoAI.Telegram.Tests/... --no-build` ?
