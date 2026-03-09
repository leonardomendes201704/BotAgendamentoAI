using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using BotAgendamentoAI.Telegram.TelegramCompat;
using BotAgendamentoAI.Telegram.TelegramCompat.Types;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.Enums;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.InputFiles;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class TelegramMessageSender
{
    private readonly ConversationHistoryService _history;

    public TelegramMessageSender(ConversationHistoryService history)
    {
        _history = history;
    }

    public async Task<Message> SendTextAsync(
        BotDbContext db,
        ITelegramBotClient bot,
        string tenantId,
        long telegramUserId,
        ChatId chatId,
        string text,
        IReplyMarkup? replyMarkup,
        long? relatedJobId,
        CancellationToken cancellationToken)
    {
        var effectiveReplyMarkup = replyMarkup ?? new ReplyKeyboardRemove();

        var sent = await bot.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: ParseMode.Default,
            replyMarkup: effectiveReplyMarkup,
            cancellationToken: cancellationToken);

        await _history.LogOutboundAsync(
            db,
            tenantId,
            telegramUserId,
            MessageType.Text,
            text,
            sent.MessageId,
            relatedJobId,
            cancellationToken);

        return sent;
    }

    public async Task<Message> SendPhotoCardAsync(
        BotDbContext db,
        ITelegramBotClient bot,
        string tenantId,
        long telegramUserId,
        ChatId chatId,
        string fileId,
        string caption,
        InlineKeyboardMarkup? buttons,
        long? relatedJobId,
        CancellationToken cancellationToken)
    {
        var sent = await bot.SendPhoto(
            chatId: chatId,
            photo: InputFile.FromString(fileId),
            caption: caption,
            parseMode: ParseMode.Default,
            replyMarkup: buttons,
            cancellationToken: cancellationToken);

        await _history.LogOutboundAsync(
            db,
            tenantId,
            telegramUserId,
            MessageType.Photo,
            caption,
            sent.MessageId,
            relatedJobId,
            cancellationToken);

        return sent;
    }

    public async Task<IReadOnlyList<Message>> SendMediaGroupAsync(
        BotDbContext db,
        ITelegramBotClient bot,
        string tenantId,
        long telegramUserId,
        ChatId chatId,
        IReadOnlyList<string> fileIds,
        string caption,
        long? relatedJobId,
        CancellationToken cancellationToken)
    {
        var media = new List<IAlbumInputMedia>();
        for (var i = 0; i < fileIds.Count; i++)
        {
            media.Add(new InputMediaPhoto(InputFile.FromString(fileIds[i]))
            {
                Caption = i == 0 ? caption : null
            });
        }

        var sent = await bot.SendMediaGroup(
            chatId: chatId,
            media: media,
            cancellationToken: cancellationToken);

        foreach (var item in sent)
        {
            await _history.LogOutboundAsync(
                db,
                tenantId,
                telegramUserId,
                MessageType.Photo,
                caption,
                item.MessageId,
                relatedJobId,
                cancellationToken);
        }

        return sent;
    }

    public async Task<Message> SendLocationAsync(
        BotDbContext db,
        ITelegramBotClient bot,
        string tenantId,
        long telegramUserId,
        ChatId chatId,
        double latitude,
        double longitude,
        long? relatedJobId,
        CancellationToken cancellationToken)
    {
        var sent = await bot.SendLocation(
            chatId,
            latitude,
            longitude,
            cancellationToken: cancellationToken);

        await _history.LogOutboundAsync(
            db,
            tenantId,
            telegramUserId,
            MessageType.Location,
            $"lat={latitude};lng={longitude}",
            sent.MessageId,
            relatedJobId,
            cancellationToken);

        return sent;
    }
}
