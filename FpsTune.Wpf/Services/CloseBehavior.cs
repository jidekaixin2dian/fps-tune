namespace FpsTune.Wpf.Services;

internal static class CloseBehavior
{
    public const string Ask = "ask", Tray = "tray", Exit = "exit";
    public static string Normalize(string? value) => value is Tray or Exit ? value : Ask;
}

internal sealed record CloseChoice(string Action, bool Remember);
