using System.Text.Json;
using System.Text.Json.Nodes;
using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Agents.ClaudeCode;

/// <summary>Claude Code, in print mode, with the work-on-issue skill as a plugin and only the Boards MCP server.</summary>
public sealed class ClaudeCodeAgent(RunnerConfig config) : ICodingAgent
{
    /// <summary>Never allowed, whatever the repository's own settings say.</summary>
    private static readonly string[] DisallowedTools =
    [
        "Bash(git push:*)",
        // With git -C <folder> (multi-repository workspaces) the subcommand isn't first: "git * push" catches any form.
        "Bash(git * push)",
        "Bash(git * push *)",
        "Bash(gh:*)",
        "WebFetch",
        "WebSearch",
    ];

    public string Key => CodingAgents.Claude;

    public string Name => "Claude Code";

    public string ClientName => "Claude Code agent";

    public string InstallUrl => "https://claude.com/claude-code";

    /// <summary>
    /// The configured path; for plain "claude", PATH (and ~/.local/bin, where the native installer puts it) first, then the
    /// Claude desktop app's newest bundled copy.
    /// </summary>
    public string FindExecutable()
    {
        if (config.ClaudePath != "claude")
        {
            return Paths.Expand(config.ClaudePath);
        }

        var onPath = CodingAgents.FindOnPath("claude");
        if (onPath is not null)
        {
            return onPath;
        }

        var bundled = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/Claude/claude-code");
        var bundledName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var newest = Directory.Exists(bundled)
            ? Directory.GetDirectories(bundled)
                .Select(x => (Dir: x, Version: Version.TryParse(Path.GetFileName(x), out var v) ? v : null))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .Select(x => Directory.GetFiles(x.Dir, bundledName, SearchOption.AllDirectories)
                    .FirstOrDefault(f => OperatingSystem.IsWindows() || f.EndsWith("Contents/MacOS/claude")))
                .FirstOrDefault(x => x is not null)
            : null;
        return newest ?? throw new FileNotFoundException("Claude Code wasn't found: put claude on PATH or set claudePath in the config.");
    }

    /// <summary>Whether Claude Code is signed in, by `claude auth status`.</summary>
    public bool IsSignedIn(string executable)
    {
        var info = new System.Diagnostics.ProcessStartInfo(executable);
        info.ArgumentList.Add("auth");
        info.ArgumentList.Add("status");
        RemoveSessionEnvironment(info);
        var result = Shell.Run(info);
        try
        {
            return JsonSerializer.Deserialize(result.Output, RunnerJson.Default.ClaudeAuthStatus)?.LoggedIn == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>`claude auth login` in this terminal - the person signs in in the browser it opens.</summary>
    public void SignIn(string executable)
    {
        var info = new System.Diagnostics.ProcessStartInfo(executable);
        info.ArgumentList.Add("auth");
        info.ArgumentList.Add("login");
        RemoveSessionEnvironment(info);
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
    }

    public AgentProcess Start(string apiKey, AgentRunSummary run, Workspace workspace, string logPath)
    {
        var info = Info(apiKey, run.IssueKey, workspace);
        AddArguments(info, ["-p", CodingAgents.Prompt("boards-agent:work-on-issue", run, workspace), .. Unattended(), .. Common(run.IssueKey, workspace)]);
        return new AgentProcess(AgentProcess.Launch(info), logPath, new ClaudeCodeOutput());
    }

    public AgentProcess Resume(string apiKey, string issueKey, Workspace workspace, string sessionId, string instruction, string logPath)
    {
        var info = Info(apiKey, issueKey, workspace);
        AddArguments(info, ["-p", instruction, "--resume", sessionId, .. Unattended(), .. Common(issueKey, workspace)]);
        return new AgentProcess(AgentProcess.Launch(info), logPath, new ClaudeCodeOutput());
    }

    public int ResumeInteractive(string apiKey, string issueKey, Workspace workspace, string sessionId, string? context)
    {
        // The terminal's own input and output: the person talks to Claude Code, which asks before anything not allowed.
        var info = Info(apiKey, issueKey, workspace);
        // The person approves what isn't allowed - not their own default mode (e.g. Auto), which would decide for them.
        AddArguments(info, ["--resume", sessionId, "--permission-mode", "default", .. Common(issueKey, workspace, interactive: true)]);
        if (context is not null)
        {
            AddArguments(info, ["--append-system-prompt", context]);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>The process in the workspace, with the API key in its environment and no Claude session state of the runner's.</summary>
    private System.Diagnostics.ProcessStartInfo Info(string apiKey, string issueKey, Workspace workspace)
    {
        var info = new System.Diagnostics.ProcessStartInfo(FindExecutable()) { WorkingDirectory = workspace.Path };
        RemoveSessionEnvironment(info);

        // The MCP config refers to the key as ${VAR}, so it reaches Claude Code only through its environment.
        info.Environment[config.ApiKeyEnv] = apiKey;
        return info;
    }

    /// <summary>What a run without a person adds: stream-json output, edits accepted, the turn and budget limits.</summary>
    private string[] Unattended()
    {
        string[] arguments =
        [
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", "acceptEdits",
            "--max-turns", config.MaxTurns.ToString(),
        ];
        return config.MaxBudgetUsd is { } budget
            ? [.. arguments, "--max-budget-usd", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)]
            : arguments;
    }

    /// <summary>The Boards MCP server and skill, the project settings only, the allowed and denied tools, the other repositories.</summary>
    private string[] Common(string issueKey, Workspace workspace, bool interactive = false)
    {
        var mcpConfigPath = Path.Combine(config.WorktreeFolder, $"{issueKey}.mcp.json");
        Directory.CreateDirectory(config.WorktreeFolder);
        File.WriteAllText(mcpConfigPath, McpConfig());
        return
        [
            "--plugin-dir", AgentPlugin.Directory.Value,
            "--mcp-config", mcpConfigPath,
            "--strict-mcp-config",
            "--setting-sources", "project",
            // The person's browser only in an interactive session they allowed it for (RunnerConfig.Chrome); otherwise
            // Claude Code would ask (interactive) or could use Claude in Chrome unattended.
            interactive && config.Chrome ? "--chrome" : "--no-chrome",
            "--allowedTools", "Skill(boards-agent:work-on-issue)", .. AllowedCommands(workspace), .. CodingAgents.BoardsTools.Select(x => $"mcp__boards__{x}"),
            "--disallowedTools", .. DisallowedTools,
            // In checkout mode the other clones aren't under the first one, where Claude Code starts.
            .. workspace.Repos.Skip(1).SelectMany(repo => new[] { "--add-dir", repo.Path }),
        ];
    }

    private static void AddArguments(System.Diagnostics.ProcessStartInfo info, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
    }

    /// <summary>
    /// The configured commands. In a workspace of several repositories, every allowed git command also in its
    /// <c>git -C &lt;folder&gt;</c> form: Claude Code always asks before <c>cd &lt;folder&gt; &amp;&amp; git ...</c> (git can run
    /// the folder's hooks), and asking means no in print mode. Other commands work after a <c>cd</c> as they are.
    /// </summary>
    private string[] AllowedCommands(Workspace workspace)
    {
        if (!workspace.IsMultiRepo)
        {
            return config.AllowedTools;
        }

        var inFolder = config.AllowedTools
            .Select(rule => System.Text.RegularExpressions.Regex.Match(rule, @"^Bash\(git ([^)]+?)(?::\*| \*)?\)$"))
            .Where(match => match.Success)
            .Select(match => $"Bash(git -C * {match.Groups[1].Value} *)");
        return [.. config.AllowedTools, .. inFolder];
    }

    /// <summary>The boards MCP server for the agent. The API key stays in the environment: Claude Code expands ${VAR}.</summary>
    private string McpConfig()
    {
        var server = new JsonObject
        {
            ["type"] = "http",
            ["url"] = config.McpUrl,
            ["headers"] = new JsonObject { ["X-Api-Key"] = $"${{{config.ApiKeyEnv}}}" },
        };
        return new JsonObject { ["mcpServers"] = new JsonObject { ["boards"] = server } }.ToJsonString();
    }

    /// <summary>A runner started from inside a Claude Code session must not hand that session's state to the Claude it starts.</summary>
    private static void RemoveSessionEnvironment(System.Diagnostics.ProcessStartInfo info)
    {
        foreach (var name in info.Environment.Keys.Where(x => x == "CLAUDECODE" || x.StartsWith("CLAUDE_CODE_") || x.StartsWith("CLAUDE_AGENT_SDK_")).ToList())
        {
            info.Environment.Remove(name);
        }
    }
}

/// <summary>`claude auth status`'s answer - only what setup reads.</summary>
public sealed record ClaudeAuthStatus(bool LoggedIn);
