# Etapa 11 - Pinos de Jobs no mapa do Dashboard

## Data
- 2026-03-06

## Problema
- Agendamento concluido em `Jobs` nao aparecia no mapa em alguns cenarios.

## Ajustes realizados
- Arquivo: `BotAgendamentoAI.Admin/Data/SqliteAdminRepository.cs`
  - Priorizacao dos rows mais recentes antes da geocodificacao.
  - Aumento de tentativas por request para novos registros (`maxGeocodeAttemptsPerRequest = 25`).
  - Leitura de coordenadas de texto (`lat=...;lng=...`) quando presente no endereco.
  - Leitura de cache geocode (`booking_geocode_cache`) tambem para IDs `job:{id}`.
  - Ao geocodificar `job:{id}` com sucesso, persiste `Latitude/Longitude` na tabela `Jobs`.

## Resultado esperado
- Novos agendamentos do fluxo marketplace passam a aparecer no mapa com mais rapidez, mesmo quando chegam inicialmente sem coordenadas persistidas.

## Validacao
- `dotnet build BotAgendamentoAI.Admin/BotAgendamentoAI.Admin.csproj` ?
