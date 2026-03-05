namespace BotAgendamentoAI.Telegram.Domain.Enums;

public enum JobStatus
{
    Draft = 0,
    Requested = 1,
    WaitingProvider = 2,
    Accepted = 3,
    OnTheWay = 4,
    Arrived = 5,
    InProgress = 6,
    Finished = 7,
    Cancelled = 8
}
