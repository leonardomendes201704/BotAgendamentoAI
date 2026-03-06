# Etapa 08 - Correcoes de menu Telegram e dashboard Admin

## Data
- 2026-03-06

## Problemas corrigidos
1. Menu de cliente/prestador sem opcoes claras no texto de resposta.
2. Fluxo com entrada numerica (`1`, `2`, `3`...) nao era interpretado nos menus.
3. Dashboard e Conversas do Admin sem dados no fluxo marketplace (dados estavam em `MessagesLog` e nao apenas em `conversation_messages`).
4. Realtime do dashboard nao reagia a mudancas de `MessagesLog`/`Jobs`.

## Alteracoes realizadas

### Telegram
- Arquivo: `BotAgendamentoAI.Telegram/Features/Client/ClientFlowHandler.cs`
  - Adicionado normalizador de entrada numerica para menu cliente.
  - Mapeamentos: `1..5` para opcoes do menu cliente.
  - Mapeamento ativo apenas em estados de menu (`C_HOME`, `C_TRACKING`, `NONE`).

- Arquivo: `BotAgendamentoAI.Telegram/Features/Provider/ProviderFlowHandler.cs`
  - Adicionado normalizador de entrada numerica para menu prestador.
  - Mapeamentos: `1..6` para opcoes do menu prestador.
  - Mapeamento ativo apenas em estados de menu (`P_HOME`, `P_ACTIVE_JOB`, `P_FEED`, `NONE`).

### Admin (dados + dashboard)
- Arquivo: `BotAgendamentoAI.Admin/Data/SqliteAdminRepository.cs`
  - `GetDashboardAsync` agora agrega dados de:
    - legado: `conversation_messages` e `bookings`
    - marketplace: `MessagesLog` e `Jobs`
  - `GetConversationThreadsAsync` agora une threads de:
    - `conversation_messages` (telefone)
    - `MessagesLog` (`tg:{telegramUserId}`)
  - `GetConversationMessagesAsync`:
    - quando thread `tg:*`, le de `MessagesLog`
    - fallback para legado em `conversation_messages`
  - `GetBookingsAsync`:
    - une `bookings` (legado) com `Jobs` (marketplace)
    - ordena por data de criacao.
  - `GetDashboardMapPinsAsync`:
    - preserva pinos de `bookings`
    - adiciona pinos de `Jobs` (quando latitude/longitude existir)
    - suporte a geocode com seguranca de existencia de tabela.
  - Novo helper de parse de data: `ParseLocalOrUtcDateTime`.

- Arquivo: `BotAgendamentoAI.Admin/Realtime/DashboardSqliteWatcher.cs`
  - Watermarks agora monitoram tambem:
    - `MessagesLog`
    - `Jobs`
    - `UserSessions` (com `Users`)
  - Mantido monitoramento legado (`conversation_messages`, `bookings`, `conversation_state`).
  - Adicionada checagem de existencia de tabela antes de consultar.

## Validacao executada
- `dotnet build BotAgendamentoAI.Telegram/BotAgendamentoAI.Telegram.csproj` ?
- `dotnet build BotAgendamentoAI.Admin/BotAgendamentoAI.Admin.csproj` ?
- `dotnet test BotAgendamentoAI.Telegram.Tests/BotAgendamentoAI.Telegram.Tests.csproj --no-build` ? (9/9)

## Como testar rapido
1. Iniciar `BotAgendamentoAI.Telegram` e enviar `/start`.
2. Escolher `Cliente` e testar digitar `1`, `2`, `4` para navegar.
3. Iniciar `BotAgendamentoAI.Admin` e abrir `Dashboard` e `Conversas` no mesmo tenant.
4. Enviar novas mensagens no Telegram e verificar atualizacao automatica no dashboard.
