using Laraue.Boards.Agent.Config;

namespace Laraue.Boards.Agent.Infrastructure;

/// <summary>
/// System notifications about runs, shown as <see cref="AppName"/>. macOS names a notification after the app that posts
/// it - osascript's would read "Script Editor" - so on macOS they come from a tiny AppleScript app the runner compiles
/// into ~/Applications the first time; clicking one opens that app again, which opens the issue. Windows: a tray
/// balloon; Linux: notify-send. Best effort: a notification that can't be shown is only logged.
/// </summary>
static class Notifications
{
    public const string AppName = "laraue-boards-agent";

    /// <summary>Bumped when the app's script changes, so an older copy is compiled again.</summary>
    private const string AppVersion = "6";

    private const string BundleId = "com.laraue.boards-agent.notifier";

    private const string AppScript = """
        -- laraue-boards-agent: shows the notifications boards-agent left as pending-*.txt files (title, text, issue URL -
        -- one per line), oldest first. Opened without any - by a click on its notification - it opens the last one's issue.
        on run
            set folderPath to (POSIX path of (path to home folder)) & "Library/Caches/laraue-boards-agent/"
            set lastPath to folderPath & "last.txt"
            set pendingFiles to paragraphs of (do shell script "cd " & quoted form of folderPath & " && ls pending-*.txt 2>/dev/null | sort || true")
            if (count of pendingFiles) is 0 or pendingFiles is {""} then
                try
                    set issueUrl to item 3 of (paragraphs of (do shell script "cat " & quoted form of lastPath))
                    if issueUrl is not "" then open location issueUrl
                end try
                return
            end if
            repeat with i from 1 to count of pendingFiles
                set filePath to folderPath & (item i of pendingFiles)
                try
                    set parts to paragraphs of (do shell script "cat " & quoted form of filePath)
                    do shell script "mv -f " & quoted form of filePath & " " & quoted form of lastPath
                    display notification (item 2 of parts) with title (item 1 of parts)
                    delay 1
                end try
            end repeat
        end run
        """;

    public static void Show(RunnerConfig config, string title, string text, string? url)
    {
        if (!config.Notifications)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                ShowMac(title, text, url);
            }
            else if (OperatingSystem.IsWindows())
            {
                ShowWindows(title, text);
            }
            else
            {
                Start("notify-send", ["-a", AppName, title, text]);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Couldn't show the notification \"{title}\": {e.Message}");
        }
    }

    private static void ShowMac(string title, string text, string? url)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var app = Path.Combine(home, "Applications", $"{AppName}.app");
        var versionFile = Path.Combine(app, "Contents", "Resources", "boards-agent-version");
        if (!File.Exists(versionFile) || File.ReadAllText(versionFile).Trim() != AppVersion)
        {
            var source = Path.Combine(Path.GetTempPath(), $"{AppName}.applescript");
            File.WriteAllText(source, AppScript);
            Directory.CreateDirectory(Path.GetDirectoryName(app)!);
            if (Directory.Exists(app))
            {
                Directory.Delete(app, recursive: true);
            }

            var compiled = Shell.Run("/usr/bin/osacompile", "-o", app, source);
            if (compiled.ExitCode != 0)
            {
                throw new InvalidOperationException($"osacompile failed: {compiled.Error.Trim()}");
            }

            // osacompile gives the app no bundle identifier, and macOS silently drops notifications from an app without
            // one. Add it and the name, sign the edited app again (ad hoc) and register it, so it shows as AppName.
            var plist = Path.Combine(app, "Contents", "Info.plist");
            foreach (var (key, value) in new[] { ("CFBundleIdentifier", BundleId), ("CFBundleName", AppName), ("CFBundleDisplayName", AppName) })
            {
                Shell.Run("/usr/bin/plutil", "-replace", key, "-string", value, plist);
            }

            SetIcon(app);

            // Before signing: a file added afterwards would break the signature.
            File.WriteAllText(versionFile, AppVersion);
            Shell.Run("/usr/bin/codesign", "--force", "--deep", "--sign", "-", app);
            Shell.Run(
                "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
                "-f",
                app);
        }

        var folder = Path.Combine(home, "Library", "Caches", AppName);
        Directory.CreateDirectory(folder);
        // One file per notification, named in order, so two close together are both shown.
        File.WriteAllText(
            Path.Combine(folder, $"pending-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}.txt"),
            $"{OneLine(title)}\n{OneLine(text)}\n{OneLine(url ?? "")}");
        // -g: in the background, so the notification doesn't take the focus; -n: a new instance even if one still runs,
        // which wouldn't run its script again.
        Shell.Run("/usr/bin/open", "-n", "-g", app);
    }

    /// <summary>
    /// The Laraue logo (embedded assets/icon.png) as the app's icon, replacing osacompile's script icon. Best effort:
    /// without it the app keeps the default icon.
    /// </summary>
    private static void SetIcon(string app)
    {
        using var png = typeof(Notifications).Assembly.GetManifestResourceStream("assets/icon.png");
        if (png is null)
        {
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), $"{AppName}-icon-{Guid.NewGuid():N}");
        var iconset = Path.Combine(work, "icon.iconset");
        Directory.CreateDirectory(iconset);
        try
        {
            var source = Path.Combine(work, "icon.png");
            using (var file = File.Create(source))
            {
                png.CopyTo(file);
            }

            foreach (var size in new[] { 16, 32, 128, 256, 512 })
            {
                Shell.Run("/usr/bin/sips", "-z", $"{size}", $"{size}", source, "--out", Path.Combine(iconset, $"icon_{size}x{size}.png"));
                if (size * 2 <= 512)
                {
                    Shell.Run("/usr/bin/sips", "-z", $"{size * 2}", $"{size * 2}", source, "--out", Path.Combine(iconset, $"icon_{size}x{size}@2x.png"));
                }
            }

            var icns = Path.Combine(app, "Contents", "Resources", "applet.icns");
            var converted = Shell.Run("/usr/bin/iconutil", "-c", "icns", iconset, "-o", icns);
            if (converted.ExitCode != 0)
            {
                Log.Error($"The notification app's icon couldn't be made: {converted.Error.Trim()}");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void ShowWindows(string title, string text)
    {
        var info = new System.Diagnostics.ProcessStartInfo("powershell");
        foreach (var argument in new[]
        {
            "-NoProfile", "-WindowStyle", "Hidden", "-Command",
            "Add-Type -AssemblyName System.Windows.Forms; "
                + "$n = New-Object System.Windows.Forms.NotifyIcon; "
                + "$n.Icon = [System.Drawing.SystemIcons]::Information; "
                + $"$n.Text = '{AppName}'; $n.Visible = $true; "
                + "$n.ShowBalloonTip(10000, $env:BOARDS_AGENT_TITLE, $env:BOARDS_AGENT_TEXT, 'Info'); "
                + "Start-Sleep -Seconds 10; $n.Dispose()",
        })
        {
            info.ArgumentList.Add(argument);
        }

        // The texts go in through the environment, so nothing in them needs escaping.
        info.Environment["BOARDS_AGENT_TITLE"] = title;
        info.Environment["BOARDS_AGENT_TEXT"] = text;
        info.UseShellExecute = false;
        System.Diagnostics.Process.Start(info)?.Dispose();
    }

    private static void Start(string file, string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        System.Diagnostics.Process.Start(info)?.Dispose();
    }

    private static string OneLine(string value)
    {
        return value.ReplaceLineEndings(" ").Trim();
    }
}
