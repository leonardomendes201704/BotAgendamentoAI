# Etapa 14 - Fallback de geocode por CEP com lat/lng garantido

## Data
- 2026-03-06

## Problema observado
- Endereco completo foi montado corretamente, mas alguns jobs ficaram sem `Latitude/Longitude`.
- Fluxo mostrava: "Nao localizei o numero exato; vou usar localizacao aproximada do CEP.", mas a coordenada aproximada nao era persistida.

## Causa raiz
- Fallback de CEP dependia de Nominatim e pode falhar por limite (`429`) ou ausencia de resultado para CEP puro.

## Correcao

### Telegram (persistencia no Job ao confirmar)
- `BotAgendamentoAI.Telegram/Features/Client/ClientFlowHandler.cs`
  - `TryGeocodeByCepAsync` agora faz cadeia de fallback:
    1. AwesomeAPI CEP (`lat/lng` direto)
    2. ViaCEP -> geocode por endereco base
    3. Nominatim por CEP puro
  - Com isso, quando geocode exato falha, a localizacao aproximada do CEP passa a preencher `Draft.Latitude/Longitude`, que vai para `Jobs` no `ConfirmDraftAsync`.

### Admin (mapa/backfill de jobs antigos)
- `BotAgendamentoAI.Admin/Data/SqliteAdminRepository.cs`
  - `TryGeocodeAddressAsync` ganhou fallback por CEP via AwesomeAPI.
  - Se o endereco textual contiver CEP (mesmo com rua/numero), tenta extrair e usar AwesomeAPI quando Nominatim falhar.
  - Facilita backfill de jobs antigos sem coordenadas no mapa.

## Validacao
- `dotnet build BotAgendamentoAI.Telegram/...` ?
- `dotnet build BotAgendamentoAI.Admin/...` ?
- `dotnet test BotAgendamentoAI.Telegram.Tests/... --no-build` ?
