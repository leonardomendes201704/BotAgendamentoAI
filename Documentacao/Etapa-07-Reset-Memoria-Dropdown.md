# Etapa 07 - Reset de memoria com dropdown de usuarios

Data: 2026-03-05

## Mudanca solicitada

Substituir campo manual de `Telegram User ID` por um `dropdown` de usuarios na tela `Configuracoes`.

## Implementacao

- `BotConfigViewModel` agora inclui `TelegramUsers`.
- Novo modelo `TelegramUserOption` (`TelegramUserId`, `DisplayLabel`).
- Repositorio:
  - novo metodo `GetTelegramUsersAsync(tenantId, limit)`.
  - consulta tabela `Users` (tenant atual), ordenando por `UpdatedAt`.
- Controller `Settings`:
  - carrega lista de usuarios no `Index`.
- View `Settings/Index`:
  - exibe `select` com usuarios Telegram.
  - se nao houver usuarios, mostra aviso e desabilita botao de reset.

## Resultado

- O reset de memoria agora e acionado escolhendo o usuario no dropdown, sem digitar id manualmente.
