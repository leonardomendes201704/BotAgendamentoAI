using BotAgendamentoAI.Telegram;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.Enums;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.InputFiles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram.TelegramCompat;

public sealed class HttpTelegramBotClient : ITelegramBotClient
{
    private readonly TelegramApiClient _apiClient;
    private readonly string _botToken;

    public HttpTelegramBotClient(TelegramApiClient apiClient, string botToken)
    {
        _apiClient = apiClient;
        _botToken = botToken;
    }

    public async Task<Message> SendMessage(
        ChatId chatId,
        string text,
        ParseMode parseMode = ParseMode.Default,
        IReplyMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.SendMessageAsync(
            _botToken,
            chatId.Identifier,
            text,
            parseMode,
            replyMarkup,
            cancellationToken);

        return response.Result ?? new Message { Chat = new Chat { Id = chatId.Identifier } };
    }

    public async Task<Message> SendPhoto(
        ChatId chatId,
        InputFile photo,
        string? caption = null,
        ParseMode parseMode = ParseMode.Default,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.SendPhotoAsync(
            _botToken,
            chatId.Identifier,
            photo.FileId,
            caption,
            parseMode,
            replyMarkup,
            cancellationToken);

        return response.Result ?? new Message { Chat = new Chat { Id = chatId.Identifier } };
    }

    public async Task<IReadOnlyList<Message>> SendMediaGroup(
        ChatId chatId,
        IEnumerable<IAlbumInputMedia> media,
        CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.SendMediaGroupAsync(
            _botToken,
            chatId.Identifier,
            media,
            cancellationToken);

        return response.Result ?? new List<Message>();
    }

    public async Task<Message> SendLocation(
        ChatId chatId,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.SendLocationAsync(
            _botToken,
            chatId.Identifier,
            latitude,
            longitude,
            cancellationToken);

        return response.Result ?? new Message { Chat = new Chat { Id = chatId.Identifier } };
    }

    public async Task AnswerCallbackQuery(
        string callbackQueryId,
        string? text = null,
        bool showAlert = false,
        CancellationToken cancellationToken = default)
    {
        await _apiClient.AnswerCallbackQueryAsync(
            _botToken,
            callbackQueryId,
            text,
            showAlert,
            cancellationToken);
    }
}
