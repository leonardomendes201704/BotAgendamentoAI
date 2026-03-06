# Etapa 10 - Correcao reply_markup null no Telegram

## Data
- 2026-03-06

## Erro observado
- Ao clicar categoria no passo `1/7`, o bot gerava:
  - `Bad Request: object expected as reply markup`

## Causa raiz
- O payload de `sendMessage` incluia `reply_markup: null` quando nao havia teclado para enviar.
- A API do Telegram rejeita esse formato em alguns cenarios.

## Correcao aplicada
- Arquivo: `BotAgendamentoAI.Telegram/TelegramApiClient.cs`
  - `JsonSerializerOptions` configurado com:
    - `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`
- Arquivo: `BotAgendamentoAI.Telegram/TelegramModels.cs`
  - Campos opcionais anotados com `JsonIgnore(Condition = WhenWritingNull)`:
    - `TelegramSendMessageRequest.ParseMode`
    - `TelegramSendMessageRequest.ReplyMarkup`
    - `TelegramSendPhotoRequest.Caption`
    - `TelegramSendPhotoRequest.ParseMode`
    - `TelegramSendPhotoRequest.ReplyMarkup`
    - `TelegramMediaItem.Caption`
    - `TelegramMediaItem.ParseMode`
    - `TelegramAnswerCallbackRequest.Text`

## Validacao
- `dotnet build BotAgendamentoAI.Telegram/BotAgendamentoAI.Telegram.csproj` ?
- `dotnet test BotAgendamentoAI.Telegram.Tests/BotAgendamentoAI.Telegram.Tests.csproj --no-build` ?

## Resultado esperado
- Clique em categoria no `1/7` deve avancar para `2/7` sem erro de `reply_markup`.
