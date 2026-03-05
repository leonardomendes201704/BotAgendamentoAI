namespace BotAgendamentoAI.Telegram.Application.Callback;

public sealed record CallbackRoute(string Raw, IReadOnlyList<string> Parts)
{
    public string Scope => Parts.Count > 0 ? Parts[0] : string.Empty;
    public string Action => Parts.Count > 1 ? Parts[1] : string.Empty;
    public string Arg1 => Parts.Count > 2 ? Parts[2] : string.Empty;
    public string Arg2 => Parts.Count > 3 ? Parts[3] : string.Empty;
    public string Arg3 => Parts.Count > 4 ? Parts[4] : string.Empty;
}

public static class CallbackDataRouter
{
    public static bool TryParse(string? callbackData, out CallbackRoute route)
    {
        route = new CallbackRoute(string.Empty, Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(callbackData))
        {
            return false;
        }

        var parts = callbackData
            .Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Take(8)
            .ToArray();

        if (parts.Length == 0)
        {
            return false;
        }

        route = new CallbackRoute(callbackData.Trim(), parts);
        return true;
    }

    public static string RoleClient() => "U:ROLE:C";
    public static string RoleProvider() => "U:ROLE:P";
    public static string RoleBoth() => "U:ROLE:B";

    public static string ClientMenuRequest() => "M:C:HOME";
    public static string ProviderMenuRequest() => "M:P:HOME";
    public static string Cancel() => "NAV:CANCEL";
    public static string Back() => "NAV:BACK";
}
