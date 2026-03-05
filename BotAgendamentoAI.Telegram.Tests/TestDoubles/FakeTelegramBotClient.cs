using BotAgendamentoAI.Telegram.TelegramCompat;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.Enums;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.InputFiles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram.Tests.TestDoubles;

internal sealed class FakeTelegramBotClient : ITelegramBotClient
{
    private long _lastMessageId = 100;

    public List<(long ChatId, string Text)> SentTexts { get; } = new();
    public List<(long ChatId, string Caption)> SentPhotos { get; } = new();
    public List<(long ChatId, double Latitude, double Longitude)> SentLocations { get; } = new();
    public List<string> CallbackAnswers { get; } = new();

    public Task<Message> SendMessage(
        ChatId chatId,
        string text,
        ParseMode parseMode = ParseMode.Default,
        IReplyMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        var cid = (long)chatId;
        SentTexts.Add((cid, text));
        return Task.FromResult(NewMessage(cid, text));
    }

    public Task<Message> SendPhoto(
        ChatId chatId,
        InputFile photo,
        string? caption = null,
        ParseMode parseMode = ParseMode.Default,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        var cid = (long)chatId;
        SentPhotos.Add((cid, caption ?? string.Empty));
        return Task.FromResult(NewMessage(cid, caption));
    }

    public Task<IReadOnlyList<Message>> SendMediaGroup(
        ChatId chatId,
        IEnumerable<IAlbumInputMedia> media,
        CancellationToken cancellationToken = default)
    {
        var cid = (long)chatId;
        var messages = media
            .Select(item =>
            {
                SentPhotos.Add((cid, item.Caption ?? string.Empty));
                return NewMessage(cid, item.Caption);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<Message>>(messages);
    }

    public Task<Message> SendLocation(
        ChatId chatId,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var cid = (long)chatId;
        SentLocations.Add((cid, latitude, longitude));
        return Task.FromResult(new Message
        {
            MessageId = Interlocked.Increment(ref _lastMessageId),
            Chat = new Chat { Id = cid },
            Location = new Location
            {
                Latitude = latitude,
                Longitude = longitude
            }
        });
    }

    public Task AnswerCallbackQuery(
        string callbackQueryId,
        string? text = null,
        bool showAlert = false,
        CancellationToken cancellationToken = default)
    {
        CallbackAnswers.Add(text ?? string.Empty);
        return Task.CompletedTask;
    }

    private Message NewMessage(long chatId, string? text)
    {
        return new Message
        {
            MessageId = Interlocked.Increment(ref _lastMessageId),
            Chat = new Chat { Id = chatId },
            Text = text
        };
    }
}
