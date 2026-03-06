# Etapa 09 - Callback de categoria sem resposta

## Data
- 2026-03-06

## Problema
- No passo `1/7 - Qual categoria do servico?`, ao clicar em categoria em alguns cenarios, o bot nao respondia.

## Causa
- No handler de callback do cliente (`C:CAT:*`), quando a categoria nao era encontrada (ex.: callback antiga/expirada), o fluxo retornava `true` em silencio, sem mensagem para o usuario.

## Correcao
- Arquivo: `BotAgendamentoAI.Telegram/Features/Client/ClientFlowHandler.cs`
  - callback `C:CAT` agora:
    - aceita busca por `Id` e tambem por chave normalizada/nome;
    - quando invalido ou nao encontrado, responde com mensagem e reexibe menu de categorias.
  - criado helper `SendCategorySelectionAsync(...)` para padronizar reexibicao da etapa 1/7.
  - `StartWizardAsync(...)` passou a reutilizar esse helper.

## Validacao
- `dotnet build BotAgendamentoAI.Telegram/BotAgendamentoAI.Telegram.csproj` ?
- `dotnet test BotAgendamentoAI.Telegram.Tests/BotAgendamentoAI.Telegram.Tests.csproj --no-build` ?

## Resultado esperado
- Ao clicar categoria, o bot deve sempre seguir para `2/7 - Descreva o problema...`.
- Se callback estiver invalida/expirada, o bot deve avisar e mostrar novamente as categorias.
