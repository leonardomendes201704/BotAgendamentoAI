using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.Enums;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.InputFiles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram.TelegramCompat;

public interface ITelegramBotClient
{
    Task<Message> SendMessage(
        ChatId chatId,
        string text,
        ParseMode parseMode = ParseMode.Default,
        IReplyMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default);

    Task<Message> SendPhoto(
        ChatId chatId,
        InputFile photo,
        string? caption = null,
        ParseMode parseMode = ParseMode.Default,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Message>> SendMediaGroup(
        ChatId chatId,
        IEnumerable<IAlbumInputMedia> media,
        CancellationToken cancellationToken = default);

    Task<Message> SendLocation(
        ChatId chatId,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default);

    Task AnswerCallbackQuery(
        string callbackQueryId,
        string? text = null,
        bool showAlert = false,
        CancellationToken cancellationToken = default);
}
