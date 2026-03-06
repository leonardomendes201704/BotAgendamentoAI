namespace BotAgendamentoAI.Telegram.Domain.Fsm;

public static class BotStates
{
    // Client
    public const string C_HOME = "C_HOME";
    public const string C_PICK_CATEGORY = "C_PICK_CATEGORY";
    public const string C_DESCRIBE_PROBLEM = "C_DESCRIBE_PROBLEM";
    public const string C_COLLECT_PHOTOS = "C_COLLECT_PHOTOS";
    public const string C_LOCATION = "C_LOCATION";
    public const string C_SCHEDULE = "C_SCHEDULE";
    public const string C_PREFERENCES = "C_PREFERENCES";
    public const string C_CONTACT_NAME = "C_CONTACT_NAME";
    public const string C_CONTACT_PHONE = "C_CONTACT_PHONE";
    public const string C_CONFIRM = "C_CONFIRM";
    public const string C_TRACKING = "C_TRACKING";
    public const string C_RATING = "C_RATING";

    // Provider
    public const string P_HOME = "P_HOME";
    public const string P_FEED = "P_FEED";
    public const string P_ORDER_DETAILS = "P_ORDER_DETAILS";
    public const string P_ACTIVE_JOB = "P_ACTIVE_JOB";
    public const string P_FINISH_JOB = "P_FINISH_JOB";
    public const string P_PROFILE_EDIT = "P_PROFILE_EDIT";
    public const string P_PORTFOLIO_UPLOAD = "P_PORTFOLIO_UPLOAD";

    // Shared
    public const string CHAT_MEDIATED = "CHAT_MEDIATED";
    public const string HUMAN_HANDOFF = "human_handoff";
    public const string U_ROLE_REQUIRED = "U_ROLE_REQUIRED";
    public const string NONE = "NONE";
}
