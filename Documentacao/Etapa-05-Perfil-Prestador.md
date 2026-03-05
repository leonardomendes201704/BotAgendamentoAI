# Etapa 05 - Perfil do prestador + testes de timeline/chat

Data: 2026-03-05

## Entrega desta etapa

### 1) Perfil do prestador (FSM `P_PROFILE_EDIT`)

Fluxos implementados via callback:

- `P:PRF:BIO`
  - entra em modo de edicao de bio.
  - valida 3..500 caracteres.
- `P:PRF:RAD`
  - entra em modo de edicao de raio.
  - valida 1..200 km.
- `P:PRF:LOC`
  - entra em modo de local base.
  - recebe localizacao Telegram e persiste `BaseLatitude/BaseLongitude`.
- `P:PRF:CAT`
  - abre seletor de categorias.
  - toggle por item: `P:CAT:{categoryId}`.
  - persistencia final: `P:CATSAVE`.

Observacoes:
- Selecao de categorias usa a tabela `service_categories`.
- `CategoriesJson` agora e salvo como JSON normalizado de nomes.
- Tela de perfil agora retorna com `InlineKeyboard` de acoes de edicao.

### 2) Testes adicionais

Foram adicionados testes cobrindo:

- timeline do prestador: transicao para `OnTheWay` e notificacao ao cliente.
- saida de chat no cliente (`J:{jobId}:CHAT:EXIT`) com reset de sessao.
- edicao de raio do prestador (callback + texto).

## Validacao

- `dotnet build BotAgendamentoAI.Telegram/...` OK.
- `dotnet test BotAgendamentoAI.Telegram.Tests/...` OK (9 testes aprovados).
