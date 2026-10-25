namespace Laraue.Boards.Agent.Infrastructure;

/// <summary>The system's own folder picker: Finder on macOS, Explorer on Windows.</summary>
static class FolderPicker
{
    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>The chosen folder, or null when the picker was cancelled or couldn't open.</summary>
    public static string? Pick(string prompt)
    {
        var info = OperatingSystem.IsMacOS()
            ? new System.Diagnostics.ProcessStartInfo("osascript")
            : new System.Diagnostics.ProcessStartInfo("powershell");
        string[] arguments = OperatingSystem.IsMacOS()
            ? [
                "-e", "on run argv",
                "-e", "return POSIX path of (choose folder with prompt (item 1 of argv))",
                "-e", "end run",
                prompt,
            ]
            : [
                "-NoProfile", "-STA", "-Command",
                "Add-Type -AssemblyName System.Windows.Forms; "
                    + "$d = New-Object System.Windows.Forms.FolderBrowserDialog; "
                    + "$d.Description = $env:BOARDS_AGENT_PICKER_PROMPT; "
                    + "if ($d.ShowDialog() -eq 'OK') { $d.SelectedPath }",
            ];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // The prompt goes in through the environment, not the command, so nothing in it needs escaping.
        info.Environment["BOARDS_AGENT_PICKER_PROMPT"] = prompt;
        var result = Shell.Run(info);
        var folder = result.Output.Trim();
        return result.ExitCode == 0 && folder.Length > 0 ? folder.TrimEnd('/') : null;
    }
}
