# Etapa 19 - Deploy Producao

## Escopo

- Empacotamento Docker do Admin e do Worker Telegram.
- `docker-compose.yml` para execucao conjunta em producao.
- Workflow de deploy automatico para `main`.
- Scripts de bootstrap/deploy da VPS.
- Exemplo de virtual host Nginx para `bottelegram.consertapramim.com`.

## Containers

- `bottelegram-admin`
  - ASP.NET Core MVC/SignalR.
  - Porta interna `8080`.
  - Publicado somente em `127.0.0.1:${ADMIN_PORT}`.

- `bottelegram-worker`
  - Worker .NET 8 com polling do Telegram.
  - Sem porta HTTP exposta.

## Variaveis de ambiente

Arquivo `.env`:

- `ADMIN_PORT`
- `ConnectionStrings__DefaultConnection`
- `Admin__DashboardRealtimePollSeconds`
- `TelegramWorker__TimeZoneId`
- `TelegramWorker__TenantIdleDelaySeconds`
- `TelegramWorker__SessionExpiryMinutes`
- `TelegramWorker__HistoryLimitPerContext`
- `TelegramWorker__EnablePhotoValidation`

## GitHub Actions

Secrets esperados no repositorio:

- `VPS_HOST`
- `VPS_PORT`
- `VPS_USER`
- `VPS_APP_DIR`
- `VPS_SSH_KEY`

## Publicacao na VPS

Diretorio padrao:

- `/opt/bottelegram`

Fluxo:

1. `git pull` no diretorio da app.
2. `docker compose up -d --build --remove-orphans`
3. Nginx faz proxy para `127.0.0.1:5300`.
4. Certbot emite HTTPS para `bottelegram.consertapramim.com`.
