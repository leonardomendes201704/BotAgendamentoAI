# BotAgendamentoAI.Telegram

Worker .NET 8 do marketplace ConsertaPraMim com integracao Telegram via **HTTP long polling** (sem webhook).

## Premissas

- Sem pacote `Telegram.Bot`.
- Sem webhook Telegram.
- Sem necessidade de HTTPS no host da aplicacao.
- Polling em `getUpdates` usando token persistido no banco.

## Como rodar local

1. Suba o Admin (HTTP):

```powershell
dotnet run --project BotAgendamentoAI.Admin/BotAgendamentoAI.Admin.csproj --launch-profile http
```

2. Configure o tenant na UI Admin:
- `Telegram Bot Token`
- `Telegram Bot ID` (opcional para exibicao)
- `Telegram Bot Username` (opcional)
- `Ativo = Sim`

3. Suba o worker Telegram:

```powershell
dotnet run --project BotAgendamentoAI.Telegram/BotAgendamentoAI.Telegram.csproj
```

## Banco e migrations

- O worker executa `Database.Migrate()` automaticamente no startup.
- Migration atual: `InitialMarketplace`.
- Banco padrao: `./data/bot.db` (ou caminho definido em `TelegramWorker:DatabasePath`).

## Configuracoes principais (appsettings)

```json
{
  "TelegramWorker": {
    "DatabasePath": "",
    "TimeZoneId": "America/Sao_Paulo",
    "TenantIdleDelaySeconds": 3,
    "SessionExpiryMinutes": 180,
    "HistoryLimitPerContext": 20,
    "EnablePhotoValidation": false
  }
}
```

## Fluxos suportados (resumo)

- Onboarding `/start` com escolha de papel: Cliente/Prestador/Ambos.
- Wizard Cliente:
  - categoria
  - descricao
  - fotos
  - endereco/localizacao
  - horario
  - preferencia
  - confirmacao
- Feed Prestador com aceite/recusa e timeline:
  - `OnTheWay`, `Arrived`, `InProgress`, `Finished`
- Chat mediado Cliente <-> Prestador com `/sairchat`.
- Portfolio Prestador:
  - upload
  - visualizacao em galeria (`sendMediaGroup`) com paginação
  - remocao de foto por callback
- Perfil Prestador (inline):
  - editar bio
  - editar categorias (seleção por botoes + salvar)
  - editar raio de atendimento
  - definir local base via localizacao Telegram

## Callback data (exemplos)

- Role:
  - `U:ROLE:C`
  - `U:ROLE:P`
  - `U:ROLE:B`
- Cliente:
  - `C:CAT:{categoryId}`
  - `C:PH:DONE`
  - `C:SCH:URG|TOD|CAL`
  - `C:DAY:yyyyMMdd`
  - `C:TIM:yyyyMMdd:HHmm`
  - `C:PRF:LOW|RAT|FAST|CHO`
  - `C:CONF:OK|EDIT|CANCEL`
  - `C:MY:{offset}`
- Prestador:
  - `P:PRF:BIO|CAT|RAD|LOC`
  - `P:CAT:{categoryId}`
  - `P:CATSAVE`
  - `P:FEED:{offset}`
  - `P:AGD:{offset}`
  - `P:POR:UP`
  - `P:POR:VW:{offset}`
  - `P:POR:RM:{offset}`
  - `P:PRD:{photoId}:{offset}`
- Jobs:
  - `J:{jobId}:DET|ACC|REJ`
  - `J:{jobId}:GAL:{offset}`
  - `J:{jobId}:CHAT`
  - `J:{jobId}:CHAT:EXIT`
  - `J:{jobId}:S:OTW|ARR|STA|FIN|DONE`

## Testes

```powershell
dotnet test BotAgendamentoAI.Telegram.Tests/BotAgendamentoAI.Telegram.Tests.csproj
```
