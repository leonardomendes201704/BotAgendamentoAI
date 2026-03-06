# Etapa 17 - Menu Cliente com botoes inline e fallback de texto

## Objetivo
- Trocar o menu do cliente (texto puro) para menu com botoes inline.
- Garantir que, ao receber texto fora do fluxo no contexto de menu, o bot repita o menu com botoes.

## Ajustes implementados
- Novo teclado inline do menu cliente:
  - `BotAgendamentoAI.Telegram/Application/Services/KeyboardFactory.cs`
  - metodo `ClientHomeActions(bool allowSwitchToProvider)`
  - callbacks:
    - `C:HOME:REQ`
    - `C:HOME:MY`
    - `C:HOME:FAV`
    - `C:HOME:HLP`
    - `C:HOME:SWP` (somente quando role = `Both`)

- Mensagem de menu cliente dinamica por role:
  - `BotAgendamentoAI.Telegram/Application/Common/BotMessages.cs`
  - `ClientHomeMenu(bool allowSwitchToProvider)`
  - texto simplificado para apenas `Menu`

- Fluxo cliente com roteamento de callback do menu:
  - `BotAgendamentoAI.Telegram/Features/Client/ClientFlowHandler.cs`
  - adicionada tratativa `C:HOME:*` em `HandleCallbackAsync`
  - adicionados helpers:
    - `HandleHomeCallbackAsync(...)`
    - `SwitchToProviderAsync(...)`
    - `SendClientHomeMenuAsync(...)`
  - fallback de texto desconhecido no contexto de menu agora responde com:
    - mensagem de erro curta
    - menu cliente com botoes inline
  - protecao extra: ao clicar `C:HOME:*` durante `CHAT_MEDIATED`, o bot encerra o estado de chat mediado antes de seguir para o menu.

- Roteamento central de callback do menu cliente:
  - `BotAgendamentoAI.Telegram/Application/Services/MarketplaceBotOrchestrator.cs`
  - callbacks `C:HOME:*` agora sao encaminhados explicitamente para o fluxo cliente, independente do modo atual.

- Padronizacao de envio do menu cliente inline em outros pontos:
  - `BotAgendamentoAI.Telegram/Application/Services/MarketplaceBotOrchestrator.cs`
  - `BotAgendamentoAI.Telegram/Features/Provider/ProviderFlowHandler.cs`
  - `BotAgendamentoAI.Telegram/Application/Services/JobWorkflowService.cs`

## Validacao
- Build:
  - `dotnet build BotAgendamentoAI.Telegram/BotAgendamentoAI.Telegram.csproj`
  - `dotnet build BotAgendamentoAI.Admin/BotAgendamentoAI.Admin.csproj`
- Testes:
  - `dotnet test BotAgendamentoAI.Telegram.Tests/BotAgendamentoAI.Telegram.Tests.csproj --no-build`

Todos passaram sem erro.
