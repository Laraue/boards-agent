using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Commands;

/// <summary>
/// `lba service`: runs the runner in the background as the signed-in user - where their credential store and
/// Claude Code sign-in are - started at login and restarted if it stops. A launchd LaunchAgent on macOS, a Task Scheduler
/// logon task on Windows. systemd isn't supported yet.
/// </summary>
public static class Service
{
    private const string Label = "com.laraue.boards-agent";

    private const string TaskName = "boards-agent";

    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public static bool IsInstalled => OperatingSystem.IsWindows()
        ? Shell.Run("schtasks", "/Query", "/TN", TaskName).ExitCode == 0
        : File.Exists(PlistPath);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string PlistPath => Path.Combine(Home, "Library", "LaunchAgents", $"{Label}.plist");

    public static string LogPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "boards-agent", "boards-agent.log")
        : Path.Combine(Home, "Library", "Logs", "boards-agent", "boards-agent.log");

    /// <summary>launchd's domain for the signed-in user's GUI session - where the Keychain is unlocked.</summary>
    private static string Domain => $"gui/{Shell.Run("/usr/bin/id", "-u").Output.Trim()}";

    public static int Run(RunnerArgs options)
    {
        if (!IsSupported)
        {
            Console.WriteLine("The background service isn't supported on this system yet. Run `lba` in a terminal instead.");
            return 1;
        }

        return options.ServiceAction switch
        {
            "install" => Install(options.ConfigPath),
            "uninstall" => Uninstall(),
            "restart" => RestartCommand(options.ConfigPath),
            _ => Status(),
        };
    }

    public static int Install(string configPath)
    {
        var executable = StableExecutablePath();
        if (executable is null)
        {
            Console.WriteLine("The service needs the installed laraue-boards-agent binary, not `dotnet run`.");
            return 1;
        }

        var config = RunnerConfig.TryLoad(configPath);
        if (config is null || (config.CodeFolders.Length == 0 && config.Spaces.Count == 0) || ApiKeySource.Resolve(config) is null)
        {
            Console.WriteLine("Run `lba setup` first: the service needs a signed-in config that knows where your code is.");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var error = OperatingSystem.IsWindows()
            ? InstallTask(executable, configPath)
            : InstallLaunchAgent(executable, configPath);
        if (error is not null)
        {
            Console.WriteLine($"The service couldn't be started: {error}");
            return 1;
        }

        Console.WriteLine($"The agent runs in the background now, and starts again when you sign in to your {(OperatingSystem.IsWindows() ? "PC" : "Mac")}.");
        Console.WriteLine($"Log: {LogPath}");
        Console.WriteLine("Hand an issue to the agent in Boards. `lba service status` shows what it's doing.");
        return 0;
    }

    /// <summary>Restarts the running service, e.g. after setup changed the config or an update replaced the binary.</summary>
    public static void Restart()
    {
        if (OperatingSystem.IsWindows())
        {
            Shell.Run("schtasks", "/End", "/TN", TaskName);
            Shell.Run("schtasks", "/Run", "/TN", TaskName);
        }
        else
        {
            Shell.Run("/bin/launchctl", "kickstart", "-k", $"{Domain}/{Label}");
        }
    }

    private static int Uninstall()
    {
        if (OperatingSystem.IsWindows())
        {
            Shell.Run("schtasks", "/End", "/TN", TaskName);
            Shell.Run("schtasks", "/Delete", "/TN", TaskName, "/F");
        }
        else
        {
            Shell.Run("/bin/launchctl", "bootout", $"{Domain}/{Label}");
            if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
            }
        }

        Console.WriteLine($"The background service is removed. The log stays at {LogPath}.");
        return 0;
    }

    /// <summary>`lba service restart` - what the install scripts run after an update, so it starts the new binary.</summary>
    private static int RestartCommand(string configPath)
    {
        if (!IsInstalled)
        {
            Console.WriteLine("The background service isn't installed, so there's nothing to restart.");
            return 0;
        }

        // Registered for another binary - e.g. boards-agent before the rename to laraue-boards-agent: register it again
        // for this one, which also starts it.
        if (StableExecutablePath() is { } executable && !RegisteredFor(executable))
        {
            Console.WriteLine("The background service runs another binary - registering it again for this one...");
            return Install(configPath);
        }

        Console.WriteLine("Restarting the background service...");
        Restart();
        Console.WriteLine($"The background service was restarted. Log: {LogPath}");
        return 0;
    }

    /// <summary>Whether the installed service starts <paramref name="executable"/>.</summary>
    public static bool RegisteredFor(string executable)
    {
        var definition = OperatingSystem.IsWindows()
            ? Shell.Run("schtasks", "/Query", "/TN", TaskName, "/XML").Output
            : File.Exists(PlistPath) ? File.ReadAllText(PlistPath) : "";
        return definition.Contains(System.Security.SecurityElement.Escape(executable), StringComparison.OrdinalIgnoreCase);
    }

    private static int Status()
    {
        if (!IsInstalled)
        {
            Console.WriteLine("The background service isn't installed. `lba service install` installs it.");
            return 1;
        }

        int exitCode;
        if (OperatingSystem.IsWindows())
        {
            var query = Shell.Run("schtasks", "/Query", "/TN", TaskName, "/FO", "LIST");
            Console.WriteLine(query.Output.Trim());
            exitCode = query.ExitCode;
        }
        else
        {
            var print = Shell.Run("/bin/launchctl", "print", $"{Domain}/{Label}");
            var lines = print.Output.Split('\n').Select(x => x.Trim()).ToList();
            var state = print.ExitCode == 0
                ? lines.FirstOrDefault(x => x.StartsWith("state = "))?["state = ".Length..] ?? "unknown"
                : "not loaded";
            var pid = lines.FirstOrDefault(x => x.StartsWith("pid = "))?["pid = ".Length..];
            Console.WriteLine($"Service: {state}{(pid is null ? "" : $" (pid {pid})")}");
            exitCode = print.ExitCode;
        }

        Console.WriteLine($"Log: {LogPath}");
        if (File.Exists(LogPath))
        {
            Console.WriteLine();
            using var log = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(log);
            foreach (var line in reader.ReadToEnd().Split('\n').Where(x => x.Length > 0).TakeLast(15))
            {
                Console.WriteLine(line.TrimEnd('\r'));
            }
        }

        return exitCode == 0 ? 0 : 1;
    }

    /// <summary>
    /// The binary to start: Homebrew's version-free link rather than the versioned Cellar path, so the service survives
    /// `brew upgrade`. Null when the runner runs as `dotnet run` rather than as a binary.
    /// </summary>
    /// <summary>
    /// The installed binary this process runs from, links resolved (lba is a link to laraue-boards-agent); null when it
    /// runs from source - through dotnet, or a build output of `dotnet run`.
    /// </summary>
    public static string? StableExecutablePath()
    {
        var path = Environment.ProcessPath;
        var normalized = path?.Replace('\\', '/');
        if (path is null
            || Path.GetFileNameWithoutExtension(path) == "dotnet"
            || normalized!.Contains("/bin/Debug/", StringComparison.Ordinal)
            || normalized.Contains("/bin/Release/", StringComparison.Ordinal)
            || normalized.Contains("/dotnet/runfile/", StringComparison.Ordinal))
        {
            return null;
        }

        // Started through a link (lba, or the old boards-agent name): the service runs the binary itself.
        return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
    }

    private static string? InstallLaunchAgent(string executable, string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, Plist(executable, configPath));

        Shell.Run("/bin/launchctl", "bootout", $"{Domain}/{Label}");

        // bootout returns before the old process has stopped (the runner fails its run in progress first); bootstrapping
        // the same label meanwhile fails with "5: Input/output error". Wait for it to go, and retry once more if needed.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && Shell.Run("/bin/launchctl", "print", $"{Domain}/{Label}").ExitCode == 0)
        {
            Thread.Sleep(500);
        }

        var bootstrap = Shell.Run("/bin/launchctl", "bootstrap", Domain, PlistPath);
        if (bootstrap.ExitCode != 0)
        {
            Thread.Sleep(2000);
            bootstrap = Shell.Run("/bin/launchctl", "bootstrap", Domain, PlistPath);
        }

        return bootstrap.ExitCode == 0 ? null : bootstrap.Error.Trim();
    }

    /// <summary>
    /// The LaunchAgent. It gets this shell's PATH, since launchd's own is minimal and Claude Code runs the project's
    /// build tools (dotnet, node, ...) through it. launchd writes the runner's output to the log.
    /// </summary>
    private static string Plist(string executable, string configPath)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/sbin:/sbin";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{Xml(executable)}</string>
                <string>--config</string>
                <string>{Xml(configPath)}</string>
              </array>
              <key>EnvironmentVariables</key>
              <dict>
                <key>PATH</key>
                <string>{Xml(path)}</string>
              </dict>
              <key>WorkingDirectory</key>
              <string>{Xml(Home)}</string>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <true/>
              <key>ThrottleInterval</key>
              <integer>60</integer>
              <key>StandardOutPath</key>
              <string>{Xml(LogPath)}</string>
              <key>StandardErrorPath</key>
              <string>{Xml(LogPath)}</string>
            </dict>
            </plist>
            """;
    }

    private static string? InstallTask(string executable, string configPath)
    {
        var definition = Path.Combine(Path.GetTempPath(), $"boards-agent-task-{Guid.NewGuid():N}.xml");
        try
        {
            // Task Scheduler reads the definition as UTF-16.
            File.WriteAllText(definition, TaskXml(executable, configPath), System.Text.Encoding.Unicode);
            Shell.Run("schtasks", "/End", "/TN", TaskName);
            var create = Shell.Run("schtasks", "/Create", "/TN", TaskName, "/XML", definition, "/F");
            if (create.ExitCode != 0)
            {
                return create.Error.Trim();
            }

            var run = Shell.Run("schtasks", "/Run", "/TN", TaskName);
            return run.ExitCode == 0 ? null : run.Error.Trim();
        }
        finally
        {
            File.Delete(definition);
        }
    }

    /// <summary>
    /// The logon task: runs as the user (so it sees their Credential Manager and Claude Code sign-in) with no time limit,
    /// restarted if it fails. conhost --headless keeps a console app from opening a window; the runner writes the log
    /// itself, since a task has no output redirection.
    /// </summary>
    private static string TaskXml(string executable, string configPath)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var arguments = $"--headless \"{executable}\" --config \"{configPath}\" --log \"{LogPath}\"";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>laraue-boards-agent: works on Laraue Boards issues handed to the agent.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Xml(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Xml(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
                <StartWhenAvailable>true</StartWhenAvailable>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>conhost.exe</Command>
                  <Arguments>{Xml(arguments)}</Arguments>
                  <WorkingDirectory>{Xml(Home)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string Xml(string value)
    {
        return System.Security.SecurityElement.Escape(value);
    }
}
