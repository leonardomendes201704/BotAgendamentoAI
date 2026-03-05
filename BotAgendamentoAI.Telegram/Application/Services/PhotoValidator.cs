namespace BotAgendamentoAI.Telegram.Application.Services;

public interface IPhotoValidator
{
    Task<PhotoValidationResult> ValidateAsync(string category, IReadOnlyList<string> photoFileIds, CancellationToken cancellationToken);
}

public sealed record PhotoValidationResult(bool Ok, string Message);

public sealed class StubPhotoValidator : IPhotoValidator
{
    public Task<PhotoValidationResult> ValidateAsync(string category, IReadOnlyList<string> photoFileIds, CancellationToken cancellationToken)
    {
        return Task.FromResult(new PhotoValidationResult(true, "validacao desativada"));
    }
}
