# Etapa 18 - Atendimento humano via Admin e opcao global no bot

## Objetivo
- Permitir que um atendente humano, pela UI Admin, intervenha em conversas e envie mensagens para cliente ou prestador sem depender de refresh manual.
- Disponibilizar a opcao de "Falar com atendente" em todos os fluxos do bot (cliente e prestador), com acionamento consistente em qualquer estado.

## Escopo funcional
- Bot Telegram:
  - Nova acao global de handoff humano, reconhecida por botao e comando textual.
  - Ao solicitar atendente, conversa entra em modo de atendimento humano e pausa respostas automatizadas daquele usuario.
  - Cliente e prestador devem conseguir acionar atendimento humano em qualquer etapa do fluxo.
- Admin:
  - Tela de detalhes da conversa com composer para mensagem de atendente.
  - Acoes para iniciar/encerrar intervencao humana.
  - Atualizacao em tempo real das mensagens e do status de intervencao.
- Dashboard:
  - KPI de conversas em atendimento humano (abertas) e historico/metricas de intervencoes.

## Premissas e regras
- Persistencia obrigatoria em banco (nao usar estado apenas em memoria/sessao).
- Parametros funcionais novos devem ficar em banco e editaveis na UI de Configuracoes do Admin.
- Datas em UTC no banco; exibicao em horario de negocio no front.
- Fluxo deve funcionar em SQLite e SQL Server.

## Proposta tecnica

### 1) Modelo de dados (novo)
- Criar tabela `tg_human_handoff_sessions`:
  - `id` (PK)
  - `tenant_id`
  - `telegram_user_id`
  - `app_user_id` (nullable, quando houver vinculo)
  - `requested_by_role` (`client|provider|both|unknown`)
  - `is_open` (bool)
  - `requested_at_utc`
  - `accepted_at_utc` (nullable)
  - `closed_at_utc` (nullable)
  - `assigned_agent` (nullable, nome/login do operador admin)
  - `previous_state` (nullable, para retorno controlado)
  - `close_reason` (nullable)
  - `last_message_at_utc`
- Indices:
  - `ix_handoff_tenant_open (tenant_id, is_open, requested_at_utc desc)`
  - `uq_handoff_open_thread (tenant_id, telegram_user_id, is_open)` para garantir no maximo 1 handoff aberto por conversa.

### 2) Bot Telegram - orquestracao
- Novo servico de dominio para handoff (ex.: `HumanHandoffService`) com operacoes:
  - `RequestAsync(...)`
  - `OpenAsync(...)` (aceite do atendente)
  - `CloseAsync(...)`
  - `GetOpenByTelegramUserAsync(...)`
- Interceptacao global no `MarketplaceBotOrchestrator`:
  - Antes do roteamento para `ClientFlowHandler`/`ProviderFlowHandler`, reconhecer:
    - texto: `falar com atendente`, `/atendente`, `atendente`
    - callback: acao dedicada (ex.: `S:ATD:REQ`)
  - Se houver handoff aberto, mensagens do usuario nao entram nos fluxos automatizados.
- Durante handoff aberto:
  - Bot apenas confirma recebimento/encaminhamento (texto configuravel).
  - Fluxos automatizados ficam pausados para aquele usuario ate fechamento do handoff.

### 3) Opcao em todos os fluxos
- Inserir atalho de atendimento humano nos pontos comuns:
  - `KeyboardFactory.NavigationRow()` (inline compartilhado) recebe botao "Falar com atendente".
  - Teclados reply de passos longos (ex.: localizacao/CEP) incluem opcao.
  - Menus principais cliente e prestador exibem opcao explicitamente.
- Reconhecimento textual global garante cobertura mesmo em fluxos legados ou teclados antigos.

### 4) Admin - intervencao ativa
- Adicionar endpoints em `ConversationsController`:
  - `POST /Conversations/Handoff/Open`
  - `POST /Conversations/Handoff/Close`
  - `POST /Conversations/SendHumanMessage`
  - `GET /Conversations/Handoff/Status`
- Implementar envio ao Telegram no Admin:
  - Servico interno (ex.: `AdminTelegramGateway`) usando token do tenant em banco.
  - Ao enviar mensagem humana:
    - despachar para Telegram
    - registrar em `tg_MessagesLog` como outbound para aparecer no historico.
- UI `Conversations/Details`:
  - Card de status do handoff (aberto/fechado, desde quando).
  - Composer (textarea + enviar).
  - Botao iniciar/encerrar atendimento.
  - Realtime mantendo feed sincronizado sem reload.

### 5) Configuracoes operacionais (Admin > Configuracoes)
- Incluir no modelo/config:
  - `HumanHandoffEnabled` (bool)
  - `HumanHandoffAutoCloseMinutes` (int, default seguro)
  - `HumanHandoffQueueText` (mensagem quando entra na fila)
  - `HumanHandoffActiveText` (mensagem durante atendimento)
  - `HumanHandoffClosedText` (mensagem ao encerrar)
- Persistir por tenant e expor na UI de Settings.

## Contratos e comportamento esperado
- Solicitacao de atendente:
  - cria/reativa handoff aberto.
  - marca contexto de conversa como humano.
  - responde ao usuario com texto configurado.
- Envio humano pelo Admin:
  - somente para threads Telegram (`tg:<id>`).
  - registra historico outbound.
  - atualiza `last_message_at_utc` do handoff.
- Encerramento:
  - fecha handoff, grava auditoria de quem encerrou.
  - retoma estado de menu do usuario de forma segura.

## Observabilidade e KPI
- Novos indicadores no dashboard:
  - `Conversas em atendimento humano (abertas)`
  - `Solicitacoes de atendimento humano (periodo)`
  - `Atendimentos encerrados (periodo)`
- Realtime para atualizar KPIs e status de conversa.

## Plano de implementacao (ordem)
1. Dados e persistencia
   - criar tabela(s), indices e compatibilidade SQLite/SQL Server.
   - repositorios para leitura/escrita de handoff.
2. Bot - handoff global
   - interceptacao global de comando/botao.
   - pausa de automacao durante handoff aberto.
3. Admin - endpoints de intervencao
   - abrir/fechar handoff e enviar mensagem humana.
4. UI Admin
   - composer, status e botoes de intervencao em `Conversations/Details`.
5. Configuracoes
   - parametros em banco + tela de Settings.
6. KPI + realtime
   - agregacoes no dashboard e atualizacao em tempo real.
7. Testes e validacao final
   - unitarios, integracao e QA manual.

## Plano de validacao
- Build/test automatizado:
  - `dotnet build BotAgendamentoAI.sln`
  - `dotnet test BotAgendamentoAI.Telegram.Tests/BotAgendamentoAI.Telegram.Tests.csproj`
- Casos funcionais:
  1. Cliente aciona "Falar com atendente" no meio de cada etapa principal e entra em handoff.
  2. Prestador aciona "Falar com atendente" no feed/agenda/perfil.
  3. Admin abre detalhe da conversa, envia mensagem e usuario recebe no Telegram.
  4. Usuario responde e mensagem aparece no Admin em tempo real.
  5. Encerrar atendimento retorna usuario ao menu correto.
  6. KPIs atualizam com solicitacoes/aberturas/encerramentos.
- Casos de falha:
  - tenant sem token ativo
  - thread nao Telegram
  - rate limit/erro da API Telegram
  - tentativa de abrir handoff duplicado.

## Riscos e mitigacoes
- Risco de concorrencia (mais de um atendente na mesma conversa):
  - mitigar com chave unica de handoff aberto + validacao otimista.
- Risco de quebra de fluxo por interceptacao global:
  - mitigar com regras de precedencia claras e testes por estado.
- Risco de spam/acoplamento forte ao Telegram:
  - mitigar com controle de envio e tratamento de erro por tenant.

## Entregaveis da proxima fase (implementacao)
- Codigo backend Telegram + Admin.
- Atualizacoes de UI no Admin.
- Atualizacoes de configuracao em Settings.
- Testes e evidencia de validacao.

## Status da implementacao (2026-03-06)
- Implementado no bot Telegram:
  - tabela `tg_human_handoff_sessions` (SQLite + SQL Server) com indice de atendimento aberto por conversa.
  - servico `HumanHandoffService` com criacao/reuso de handoff aberto e pausa de automacao via estado `human_handoff`.
  - interceptacao global no `MarketplaceBotOrchestrator` para:
    - texto (`/atendente`, `atendente`, `falar com atendente`, etc.);
    - callback inline (`S:ATD:REQ`);
    - bloqueio de fluxo automatico enquanto handoff aberto.
  - opcao "Falar com atendente" adicionada em teclados compartilhados (cliente, prestador, navegacao inline, CEP/localizacao).
- Implementado no Admin:
  - novos endpoints em `ConversationsController`:
    - `POST /Conversations/HandoffOpen`
    - `POST /Conversations/HandoffClose`
    - `POST /Conversations/SendHumanMessage`
    - `GET /Conversations/HandoffStatus`
  - tela `Conversations/Details` com:
    - status de atendimento humano em tempo real;
    - botoes abrir/encerrar atendimento;
    - composer para envio de mensagem humana sem refresh.
  - envio para Telegram usando token do tenant e registro outbound em `tg_MessagesLog` com role visual de atendente no historico.
  - repositorios SQLite/SQL Server atualizados com operacoes de handoff e envio humano.
  - watchers de realtime atualizados para monitorar watermark de `tg_human_handoff_sessions`.

## Validacao executada
- Build da solution:
  - `dotnet build BotAgendamentoAI.sln`
- Testes Telegram:
  - `dotnet test BotAgendamentoAI.Telegram.Tests/BotAgendamentoAI.Telegram.Tests.csproj`
- Testes adicionados:
  - `HumanHandoffServiceTests` cobrindo criacao de handoff e idempotencia em nova solicitacao.
