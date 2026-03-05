# Etapa 02 - Arquitetura base marketplace (HTTP puro)

Data: 2026-03-05

## Diretriz aplicada

- **Sem pacote Telegram.Bot**.
- Integracao mantida em **HTTP (long polling)** com cliente proprio.
- Sem webhook e sem necessidade de HTTPS no servidor da aplicacao.

## Base implementada

### Camadas no projeto Telegram

- `Domain`
  - Entidades marketplace: `Users`, `ProvidersProfile`, `ProviderPortfolioPhotos`, `Jobs`, `JobPhotos`, `MessagesLog`, `Ratings`, `UserSessions`.
  - Enums de dominio e estados FSM.
- `Application`
  - Roteador de callback (`CallbackDataRouter`)
  - Orquestrador (`MarketplaceBotOrchestrator`)
  - Servicos de historico, contexto, FSM, mensageria e workflow de jobs.
  - Templates centralizados (`BotMessages`).
- `Features`
  - `Client` (wizard de pedido)
  - `Provider` (feed/timeline/portfolio/perfil)
  - `Shared` (chat mediado)
- `Infrastructure`
  - `BotDbContext` (EF Core SQLite)
  - `TenantConfigService` (configuracoes Telegram por tenant)

### Compatibilidade HTTP Telegram

Foi criada uma camada `TelegramCompat` com:

- Tipos de update/message/callback/markup
- Interface `ITelegramBotClient`
- Implementacao `HttpTelegramBotClient`

Tudo trafega via `TelegramApiClient` (HTTP JSON) para os metodos:

- `getUpdates`
- `sendMessage`
- `sendPhoto`
- `sendMediaGroup`
- `sendLocation`
- `answerCallbackQuery`

## Persistencia EF Core

- Projeto migrado para `.NET 8`.
- EF Core SQLite configurado.
- Migration inicial gerada em:
  - `Infrastructure/Persistence/Migrations/InitialMarketplace*`

## Observabilidade

- Serilog configurado via `appsettings*.json`.

## Status desta etapa

- Worker compila com sucesso em HTTP puro.
- Base de arquitetura e tabelas de marketplace criadas.
- Proxima etapa: completar fluxos E2E e adicionar testes automatizados.
