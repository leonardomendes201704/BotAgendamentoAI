# Etapa 12 - CEP puro no endereco e pino no mapa

## Data
- 2026-03-06

## Diagnostico confirmado no banco
- Job criado com endereco somente CEP:
  - `Jobs.Id=2`
  - `AddressText = 11704150`
  - `Latitude = NULL`
  - `Longitude = NULL`
- Geocode cache registrou falha:
  - `booking_id = job:2`
  - `status = failed`
  - `error_message = Nenhum resultado.`

## Causa raiz
- No fluxo marketplace, ao receber apenas CEP no passo de localizacao, o bot salvava o texto bruto sem resolver ViaCEP.
- Com endereco incompleto, o geocoder do dashboard pode falhar e nao criar pin.

## Correcao aplicada

### Telegram (captura de endereco)
- Arquivo: `BotAgendamentoAI.Telegram/Features/Client/ClientFlowHandler.cs`
  - Se usuario enviar somente CEP:
    - consulta ViaCEP;
    - monta endereco base;
    - pede apenas numero/complemento;
    - depois monta `AddressText` completo e avanca para agenda.
  - Novos helpers: `LookupCepAsync`, `BuildAddressFromCep`, `MergeAddressWithNumber`, `AdvanceToScheduleAsync`.
- Arquivo: `BotAgendamentoAI.Telegram/Domain/Fsm/UserDraft.cs`
  - Novos campos temporarios:
    - `AddressBaseFromCep`
    - `WaitingAddressNumber`

### Admin (mapa/geocode)
- Arquivo: `BotAgendamentoAI.Admin/Data/SqliteAdminRepository.cs`
  - Geocode agora tenta ViaCEP quando `address` for apenas CEP (`TryNormalizeCepOnly` + `TryResolveAddressByCepAsync`).
  - Mantida estrategia de extrair `lat/lng` de texto quando existir.
  - Persistencia de coordenadas em `Jobs` quando geocode de `job:{id}` tiver sucesso.

## Validacao
- `dotnet build BotAgendamentoAI.Telegram/...` ?
- `dotnet build BotAgendamentoAI.Admin/...` ?
- `dotnet test BotAgendamentoAI.Telegram.Tests/... --no-build` ?

## Resultado esperado
- Novos pedidos com CEP puro: bot resolve endereco e nao fecha pedido com `AddressText` apenas CEP.
- Pedidos antigos com CEP puro: dashboard passa a tentar ViaCEP + geocode e tende a gerar pin.
