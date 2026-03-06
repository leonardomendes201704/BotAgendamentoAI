# Etapa 13 - CEP obrigatorio + confirmacao de endereco + geocode exato

## Data
- 2026-03-06

## Diagnostico do problema
- Job salvo com `AddressText` contendo apenas CEP (`11704150`) e sem coordenadas.
- Nessa condicao, o geocode pode falhar e o pin nao aparece no mapa.

## Regras implementadas (como solicitado)
1. No passo de endereco, o bot aceita **somente CEP**.
2. Depois de resolver CEP (ViaCEP), o bot mostra endereco base e pede **apenas numero/complemento**.
3. Em seguida, mostra o endereco completo e exige confirmacao por botoes:
   - `Correto`
   - `Alterar`
4. Fonte de verdade do endereco:
   - `endereco resolvido do CEP + numero/complemento`.
5. Geocode:
   - tenta geocode exato com endereco completo;
   - se falhar, usa fallback de geocode do CEP.

## Arquivos alterados
- `BotAgendamentoAI.Telegram/Domain/Fsm/UserDraft.cs`
  - novo campo de controle: `WaitingAddressConfirmation`
- `BotAgendamentoAI.Telegram/Application/Common/BotMessages.cs`
  - mensagem de localizacao agora pede CEP apenas
- `BotAgendamentoAI.Telegram/Application/Services/KeyboardFactory.cs`
  - novo teclado `CepRequestKeyboard()`
  - novo teclado de confirmacao `AddressConfirmation()` (`C:ADDR:OK` / `C:ADDR:EDIT`)
- `BotAgendamentoAI.Telegram/Features/Client/ClientFlowHandler.cs`
  - fluxo completo CEP -> numero -> confirmacao
  - callbacks de confirmacao/alteracao do endereco
  - geocode exato + fallback CEP
  - rejeicao de entrada fora da regra (ex.: endereco livre no passo de CEP)

## Admin - Agendamentos
- `BotAgendamentoAI.Admin/Models/AdminModels.cs`
  - `BookingListItem` com `Latitude` e `Longitude`
- `BotAgendamentoAI.Admin/Data/SqliteAdminRepository.cs`
  - `GetBookingsAsync` agora retorna endereco + lat/lng para legado e Jobs
- `BotAgendamentoAI.Admin/Views/Bookings/Index.cshtml`
  - tabela mostra `Endereco` e `Geo (lat,lng)`

## Validacao
- `dotnet build BotAgendamentoAI.Telegram/...` ?
- `dotnet build BotAgendamentoAI.Admin/...` ?
- `dotnet test BotAgendamentoAI.Telegram.Tests/... --no-build` ?
