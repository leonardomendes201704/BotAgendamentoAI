# Etapa 15 - Geocode persistente no Job (fallback robusto)

## Data
- 2026-03-06

## Evidencia no banco
- Jobs recentes (#3 e #4) estavam com `Latitude/Longitude = NULL`.
- `booking_geocode_cache` registrou falha `HTTP 429` no Nominatim.

## Causa
- Mesmo com endereco resolvido, fallback por CEP ainda dependia de Nominatim em alguns cenarios.
- Com `429`, o fallback nao preenchia coordenadas no draft/job.

## Correcao aplicada

### Telegram (persistir lat/lng no momento da coleta de endereco)
- `BotAgendamentoAI.Telegram/Features/Client/ClientFlowHandler.cs`
  - `TryGeocodeByCepAsync` agora usa cadeia:
    1. `cep.awesomeapi.com.br` (lat/lng direto por CEP)
    2. ViaCEP -> geocode por endereco base
    3. Nominatim por CEP
  - Assim, mesmo quando Nominatim limita, o fallback por CEP tende a preencher coordenadas e gravar no `Job`.

### Admin (exibicao de lat/lng em Agendamentos)
- `BotAgendamentoAI.Admin/Data/SqliteAdminRepository.cs`
  - `GetBookingsAsync` para Jobs agora usa `COALESCE(j.Latitude, cache.latitude)` e `COALESCE(j.Longitude, cache.longitude)` com join em `booking_geocode_cache`.
  - Mesmo antes do backfill em `Jobs`, a tela pode mostrar geocode vindo do cache.

## Validacao
- `dotnet build BotAgendamentoAI.Telegram/...` ?
- `dotnet build BotAgendamentoAI.Admin/...` ?
- `dotnet test BotAgendamentoAI.Telegram.Tests/... --no-build` ?
