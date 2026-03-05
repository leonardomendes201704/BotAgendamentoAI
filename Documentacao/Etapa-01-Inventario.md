# Etapa 01 - Inventario do projeto atual e plano de refatoracao

Data: 2026-03-05

## Inventario rapido (estado atual)

- Solution atual com 3 projetos:
  - `BotAgendamentoAI` (Console, .NET 9, logica de secretaria e OpenAI)
  - `BotAgendamentoAI.Telegram` (Worker, .NET 9, polling Telegram via HTTP manual)
  - `BotAgendamentoAI.Admin` (ASP.NET Core MVC, .NET 8)
- Persistencia atual do bot baseada em ADO.NET/SQLite (`ConversationRepository`, `SqliteBookingStore`) sem EF Core.
- Integracao Telegram atual:
  - Long polling multi-tenant.
  - Entra texto e envia texto.
  - Nao possui callbacks inline, FSM robusta por perfil (cliente/prestador), cards de foto, galeria, chat mediado E2E de marketplace.
- UI Admin atual ja persiste configuracoes em banco (`tenant_telegram_config`, `tenant_bot_config`, `shared_settings`).

## Gaps para objetivo marketplace

- Falta modelagem de dominio para marketplace (Users, ProviderProfile, Jobs, Ratings, Portfolio etc.).
- Falta EF Core + migrations.
- Falta camada de aplicacao com FSM por usuario (Client/Provider).
- Falta callback router padronizado e menus inline completos.
- Falta fluxo completo cliente/prestador e timeline operacional.
- Falta chat mediado cliente <-> prestador.
- Falta log completo de mensagens/eventos no modelo pedido (MessagesLog).
- Falta testes unitarios para roteamento/estado/criacao-confirmacao de job.

## Estrategia de refatoracao (sem reiniciar do zero desnecessariamente)

1. **Migrar `BotAgendamentoAI.Telegram` para .NET 8** mantendo cliente HTTP proprio (long polling), sem `Telegram.Bot`.
2. **Introduzir arquitetura em camadas dentro do projeto Telegram**:
   - `Domain`
   - `Application`
   - `Infrastructure`
   - `Features/Client` e `Features/Provider`
3. **Adicionar EF Core SQLite com migrations** mantendo DB compartilhado com Admin.
4. **Implementar FSM persistida** (`UserSession` + `Draft`) e `CallbackData` curto com roteador central.
5. **Implementar fluxos E2E**:
   - onboarding role
   - wizard cliente
   - feed/timeline prestador
   - chat mediado
   - portfolio com galeria/paginacao
6. **Historico/contexto**: log inbound/outbound/eventos e consultas de contexto (24h ou por job).
7. **Observabilidade** com Serilog estruturado.
8. **Testes unitarios** para callback/state/job draft.

## Observacoes de compatibilidade

- Configuracoes de token Telegram e demais parametros continuarao lidas do banco para manter aderencia ao Admin atual.
- O projeto Console legado permanece, mas o foco desta refatoracao e o worker Telegram marketplace.
