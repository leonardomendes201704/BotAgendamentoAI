# Etapa 06 - Admin: reset de memoria por Telegram user

Data: 2026-03-05

## Objetivo

Permitir resetar memoria de usuario Telegram pela UI Admin sem uso manual de SQLite.

## O que foi implementado

- Menu renomeado de `Menu e mensagens` para `Configuracoes`.
- Nova acao no `SettingsController`:
  - `POST /Settings/ResetTelegramMemory`
- Nova opcao na tela de configuracoes:
  - campo `Telegram User ID`
  - checkbox `Limpar historico de mensagens`
  - botao `Resetar memoria do usuario`
- Mensagens de feedback na UI via `TempData` (sucesso/erro).

## Persistencia afetada no reset

Para o tenant selecionado e `telegramUserId` informado:

- `UserSessions`: limpa estado/draft/chat e volta para `NONE`.
- `MessagesLog`: remove historico Telegram (quando marcado para limpar historico).
- `conversation_messages` (legacy): remove por `phone = telegramUserId` e `phone = tg:telegramUserId` (quando marcado).
- `conversation_state` (legacy): remove por `phone = telegramUserId` e `phone = tg:telegramUserId`.

## Observacoes

- O reset nao remove jobs/agendamentos.
- O reset nao depende de scripts externos nem acesso manual ao SQLite.
