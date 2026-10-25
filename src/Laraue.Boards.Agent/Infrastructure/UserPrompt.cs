namespace Laraue.Boards.Agent.Infrastructure;

/// <summary>
/// Asks the person a yes/no question: in the terminal when the runner runs in one, else - as a background service - in
/// a system dialog (macOS, Windows). Without either, the answer is no.
/// </summary>
static class UserPrompt
{
    /// <summary>A dialog nobody answers counts as no after this long, so the runner doesn't wait forever.</summary>
    private const int DialogTimeoutSeconds = 600;

    /// <summary>True for <paramref name="yes"/>, false for <paramref name="no"/> or no answer.</summary>
    public static bool Ask(string message, string yes, string no)
    {
        if (!Console.IsInputRedirected)
        {
            Console.Write($"{message} [y/N] ");
            return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
        }

        if (OperatingSystem.IsMacOS())
        {
            return AskMac(message, yes, no);
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsDialog.Ask(message);
        }

        Log.Info("No terminal or dialog to ask in - taken as no.");
        return false;
    }

    /// <summary>osascript's own dialog: a LaunchAgent runs in the person's login session, so it shows on their screen.</summary>
    private static bool AskMac(string message, string yes, string no)
    {
        var info = new System.Diagnostics.ProcessStartInfo("osascript");
        // The texts go in as arguments, not into the script, so nothing in them needs escaping.
        foreach (var argument in new[]
        {
            "-e", "on run argv",
            "-e", $"display dialog (item 1 of argv) with title \"{Notifications.AppName}\" buttons {{(item 3 of argv), (item 2 of argv)}} default button 2 giving up after {DialogTimeoutSeconds}",
            "-e", "return button returned of result",
            "-e", "end run",
            message, yes, no,
        })
        {
            info.ArgumentList.Add(argument);
        }

        var result = Shell.Run(info);
        return result.ExitCode == 0 && result.Output.Trim() == yes;
    }
}
