# Checkpoint - 2026-03-05 (HTTP only)

## Estado salvo

- Refatoracao do worker marketplace em `.NET 8` com arquitetura em camadas (`Domain/Application/Infrastructure/Features`).
- Persistencia com `EF Core + SQLite` e migration inicial criada.
- Integracao Telegram em **HTTP long polling** com cliente proprio (`TelegramApiClient` + `TelegramCompat`), sem `Telegram.Bot`.
- Admin ajustado para nao forcar HTTPS (`UseHttpsRedirection` e `HSTS` removidos).
- Documentacao inicial das etapas criada em `Documentacao/Etapa-01..03`.

## Build

- `BotAgendamentoAI.Telegram`: compilando sem erros.
- `BotAgendamentoAI.Admin`: compilando sem erros.

## Pendencias para retomar

- Completar fluxo E2E marketplace (cliente/prestador) com paginacao e edge cases.
- Adicionar testes unitarios (callback router, FSM, draft->confirm).
- Atualizar README operacional (run/migrations/fluxos).
- Fazer commits curtos adicionais por feature e push.

## Diretriz fixa

- Manter premissa: **sem webhook/HTTPS no host da aplicacao, operar com polling HTTP**.
