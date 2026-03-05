using BotAgendamentoAI.Telegram.TelegramCompat.Types.Enums;

namespace BotAgendamentoAI.Telegram.TelegramCompat.Types.InputFiles;

public sealed class InputFile
{
    public string FileId { get; set; } = string.Empty;

    public static InputFile FromString(string fileId)
    {
        return new InputFile { FileId = fileId ?? string.Empty };
    }
}

public interface IAlbumInputMedia
{
    string Type { get; }
    string Media { get; }
    string? Caption { get; }
    ParseMode? ParseMode { get; }
}

public sealed class InputMediaPhoto : IAlbumInputMedia
{
    public InputMediaPhoto(InputFile file)
    {
        Media = file.FileId;
    }

    public string Type => "photo";
    public string Media { get; }
    public string? Caption { get; set; }
    public ParseMode? ParseMode { get; set; }
}
