# Etapa 03 - Diretriz HTTP only (sem HTTPS/Webhook)

Data: 2026-03-05

## Premissa consolidada

- Nao usar pacote `Telegram.Bot`.
- Nao usar webhook Telegram.
- Operar somente com **HTTP long polling** (`getUpdates`) no worker.
- Nao depender de HTTPS no host da aplicacao.

## Ajustes aplicados

- `BotAgendamentoAI.Admin/Program.cs`
  - Removido `app.UseHttpsRedirection()`.
  - Removido `app.UseHsts()`.
- `BotAgendamentoAI.Admin/Properties/launchSettings.json`
  - Removido perfil `https`.
  - Mantido perfil `http` como padrao.
- Camada de compatibilidade HTTP do Telegram mantida via `TelegramApiClient` + `TelegramCompat`.
- Namespace da camada compativel foi desvinculado de `Telegram.Bot.*` para evitar ambiguidade.

## Observacao tecnica

- A API oficial do Telegram e HTTPS no endpoint externo (`api.telegram.org`), mas isso nao exige HTTPS no seu servidor quando o modo e polling.
