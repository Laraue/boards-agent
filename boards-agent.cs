#!/usr/bin/env dotnet
// boards-agent: picks up Laraue Boards issues handed to the agent and works on them with Claude Code.
// Run: dotnet run boards-agent.cs -- [--config boards-agent.json] [--once] [--dry-run] [--check] [--version]
#:package ModelContextProtocol.Core@1.4.0
#:property PublishAot=false

using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var options = RunnerArgs.Parse(args);
if (options.Version)
{
    Console.WriteLine($"boards-agent {RunnerVersion.Value}");
    return 0;
}

var config = RunnerConfig.Load(options.ConfigPath);
if (options.Check)
{
    Console.WriteLine($"boards-agent {RunnerVersion.Value}");
    Console.WriteLine($"Claude Code: {ClaudeExecutable.Resolve(config.ClaudePath)}");
    Console.WriteLine($"Agent skill: {AgentPlugin.Directory.Value}");
}

var apiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
if (string.IsNullOrWhiteSpace(apiKey))
{
    Log.Error($"Set the {config.ApiKeyEnv} environment variable to a Boards API key.");
    return 1;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

await using var boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey, stop.Token);

var me = await boards.GetMe(stop.Token);
Log.Info($"Connected to {config.McpUrl} as {me.User.DisplayName} ({me.User.Id}), organization {me.Organization.Name}.");
if (options.Check)
{
    return 0;
}

Log.Info($"Watching spaces: {string.Join(", ", config.Spaces.Keys)}.{(options.DryRun ? " Dry run - nothing is claimed." : $" Claude Code: {ClaudeExecutable.Resolve(config.ClaudePath)}")}");

var runner = new Runner(boards, config);
try
{
    while (!stop.IsCancellationRequested)
    {
        var worked = false;
        foreach (var (spaceKey, space) in config.Spaces)
        {
            var runs = await boards.ListReadyRuns(spaceKey, me.User.Id, stop.Token);
            foreach (var run in runs)
            {
                Log.Info($"{spaceKey}: Ready run {run.Id} for {run.IssueKey} \"{run.IssueTitle}\" (handed off by {run.CreatedBy} at {run.CreatedAt:u}).");
                if (!options.DryRun && await runner.TryProcess(spaceKey, space, run, stop.Token))
                {
                    // One run at a time: poll again from the start once it's over.
                    worked = true;
                    break;
                }
            }

            if (worked)
            {
                break;
            }
        }

        if (options.Once)
        {
            break;
        }

        if (!worked)
        {
            await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), stop.Token);
        }
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
}

Log.Info("Stopped.");
return 0;

/// <summary>Takes one run from claim to finish.</summary>
sealed class Runner(BoardsClient boards, RunnerConfig config)
{
    private const int MaxReasonLength = 4096;

    /// <summary>Claims the run and works on it. False when it couldn't be claimed (taken, cancelled or blocked).</summary>
    public async Task<bool> TryProcess(string spaceKey, SpaceConfig space, AgentRunSummary run, CancellationToken ct)
    {
        if (!await boards.ClaimAgentRun(run.Id, ct))
        {
            Log.Info($"{run.IssueKey}: run {run.Id} wasn't claimed (taken, cancelled or blocked meanwhile) - skipping.");
            return false;
        }

        Log.Info($"{run.IssueKey}: claimed run {run.Id}.");
        Worktree worktree;
        try
        {
            var statuses = await FindEpicStatuses(spaceKey, run.IssueKey, ct);
            var inProgress = statuses.Statuses.First(x => x.CategoryId == StatusCategories.InProgress);

            worktree = Git.CreateWorktree(space.Repo, space.BaseRef, config.WorktreeFolder, run.IssueKey);
            Log.Info($"{run.IssueKey}: worktree {worktree.Path} on branch {worktree.Branch}.");

            await boards.EditIssueStatus(
                run.IssueKey,
                inProgress.Id,
                $"boards-agent picked this up on {Environment.MachineName}, branch `{worktree.Branch}`.",
                ct);
            Log.Info($"{run.IssueKey}: moved to {inProgress.Name} ({statuses.EpicName}).");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"{run.IssueKey}: {e.Message}");
            await Fail(run, "Couldn't start the work", e.Message);
            return true;
        }

        await Work(spaceKey, run, worktree, ct);
        return true;
    }

    /// <summary>
    /// Runs Claude Code in the worktree until it exits, the run is cancelled, or the timeout passes. The agent
    /// reports and finishes the run itself; the runner only finishes it when the agent couldn't.
    /// </summary>
    private async Task Work(string spaceKey, AgentRunSummary run, Worktree worktree, CancellationToken ct)
    {
        var logPath = Path.Combine(config.WorktreeFolder, $"{run.IssueKey}.run-{run.Id}.jsonl");
        using var agent = ClaudeAgent.Start(config, spaceKey, run, worktree, logPath);
        Log.Info($"{run.IssueKey}: Claude Code started (pid {agent.ProcessId}), log {logPath}.");

        var deadline = DateTime.UtcNow.AddMinutes(config.TimeoutMinutes);
        while (true)
        {
            Task tick = Task.Delay(TimeSpan.FromSeconds(config.CancelCheckSeconds), CancellationToken.None);
            Task exited = agent.WaitForExitAsync();
            var stopped = Task.Delay(Timeout.Infinite, ct);
            var first = await Task.WhenAny(exited, tick, stopped);

            if (first == exited)
            {
                break;
            }

            if (first == stopped)
            {
                agent.Kill();
                Log.Info($"{run.IssueKey}: the runner was stopped - Claude Code killed.");
                await Fail(run, "The runner was stopped", "boards-agent was shut down on the developer's machine before the work was finished.");
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                agent.Kill();
                Log.Info($"{run.IssueKey}: timed out after {config.TimeoutMinutes} min - Claude Code killed.");
                await Fail(run, $"Timed out after {config.TimeoutMinutes} min", agent.Summary());
                return;
            }

            var state = await GetRunState(run, ct);
            if (state == AgentRunStates.Cancelled)
            {
                agent.Kill();
                Log.Info($"{run.IssueKey}: run {run.Id} was cancelled - Claude Code killed.");
                return;
            }
        }

        var finalState = await GetRunState(run, CancellationToken.None);
        Log.Info($"{run.IssueKey}: Claude Code exited ({agent.ExitCode}), run {run.Id} is {finalState}. {agent.Summary()}");
        if (finalState == AgentRunStates.Working)
        {
            await Fail(run, "Claude Code stopped without finishing", $"Exit code {agent.ExitCode}. {agent.Summary()}");
        }
        else if (finalState is AgentRunStates.Done or AgentRunStates.Failed)
        {
            // The agent can't know what it cost; the runner adds it under the agent's own summary.
            await boards.CreateComment(run.IssueKey, $"_boards-agent: {agent.Summary()}_", CancellationToken.None);
        }
    }

    /// <summary>The run's state now, or null when it isn't the issue's latest run anymore.</summary>
    private async Task<string?> GetRunState(AgentRunSummary run, CancellationToken ct)
    {
        var issue = await boards.GetIssue(run.IssueKey, ct);
        return issue.LatestAgentRun?.Id == run.Id ? issue.LatestAgentRun.State : null;
    }

    /// <summary>The statuses of the issue's epic, in board order.</summary>
    private async Task<EpicStatusSummary> FindEpicStatuses(string spaceKey, string issueKey, CancellationToken ct)
    {
        var issue = await boards.GetIssue(issueKey, ct);
        var epics = await boards.ListStatuses(spaceKey, ct);
        return epics.FirstOrDefault(x => x.EpicId == issue.EpicId)
            ?? throw new InvalidOperationException($"the statuses of {issueKey}'s epic weren't found in {spaceKey}");
    }

    /// <summary>
    /// Fails the run: the short reason goes on the run (the web app shows it on the agent badge), the details in a
    /// comment. Not cancellable: a run left Working would keep the issue locked.
    /// </summary>
    private async Task Fail(AgentRunSummary run, string reason, string details)
    {
        var comment = $"""
            **Agent run failed: {reason}.**

            {details}

            Move the issue back to a not-started status to hand it to the agent again.
            """;
        if (comment.Length > MaxReasonLength)
        {
            comment = comment[..MaxReasonLength];
        }

        try
        {
            await boards.CreateComment(run.IssueKey, comment, CancellationToken.None);
            if (await boards.FinishAgentRun(run.Id, AgentRunStates.Failed, reason, CancellationToken.None))
            {
                Log.Info($"{run.IssueKey}: run {run.Id} finished as Failed.");
            }
            else
            {
                Log.Info($"{run.IssueKey}: run {run.Id} was cancelled meanwhile.");
            }
        }
        catch (Exception e)
        {
            Log.Error($"{run.IssueKey}: couldn't report the failure: {e.Message}");
        }
    }
}

/// <summary>Agent run states, as the MCP tools name them.</summary>
static class AgentRunStates
{
    public const string Working = "Working";
    public const string Done = "Done";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

/// <summary>Status category ids, as list_statuses returns them.</summary>
static class StatusCategories
{
    public const int Created = 0;
    public const int InProgress = 1;
    public const int Completed = 2;
    public const int Review = 3;
}

/// <summary>A Claude Code process working on one run, in print mode, its stream-json output going to a log file.</summary>
sealed class ClaudeAgent : IDisposable
{
    /// <summary>The Boards MCP tools the agent may call. Not edit_issue/delete_issue (locked anyway) or the run tools other than finish.</summary>
    private static readonly string[] BoardsTools =
    [
        "get_me",
        "get_issue",
        "list_issue_comments",
        "get_issue_history",
        "get_attachment",
        "list_issues",
        "list_statuses",
        "list_epics",
        "list_members",
        "edit_issue_status",
        "create_comment",
        "finish_agent_run",
    ];

    /// <summary>Never allowed, whatever the repository's own settings say.</summary>
    private static readonly string[] DisallowedTools =
    [
        "Bash(git push:*)",
        "Bash(gh:*)",
        "WebFetch",
        "WebSearch",
    ];

    private readonly System.Diagnostics.Process _process;
    private readonly StreamWriter _log;
    private readonly Task _pump;
    private string? _resultLine;

    private ClaudeAgent(System.Diagnostics.Process process, StreamWriter log)
    {
        _process = process;
        _log = log;
        _pump = Task.Run(Pump);
    }

    public int ProcessId => _process.Id;

    public int ExitCode => _process.ExitCode;

    public static ClaudeAgent Start(RunnerConfig config, string spaceKey, AgentRunSummary run, Worktree worktree, string logPath)
    {
        var mcpConfigPath = Path.Combine(config.WorktreeFolder, $"{run.IssueKey}.mcp.json");
        File.WriteAllText(mcpConfigPath, McpConfig(config));

        var info = new System.Diagnostics.ProcessStartInfo(ClaudeExecutable.Resolve(config.ClaudePath))
        {
            WorkingDirectory = worktree.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] arguments =
        [
            "-p", Prompt(spaceKey, run, worktree),
            "--plugin-dir", AgentPlugin.Directory.Value,
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", "acceptEdits",
            "--max-turns", config.MaxTurns.ToString(),
            "--mcp-config", mcpConfigPath,
            "--strict-mcp-config",
            "--setting-sources", "project",
            "--allowedTools", "Skill(boards-agent:work-on-issue)", .. config.AllowedTools, .. BoardsTools.Select(x => $"mcp__boards__{x}"),
            "--disallowedTools", .. DisallowedTools,
        ];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (config.MaxBudgetUsd is { } budget)
        {
            info.ArgumentList.Add("--max-budget-usd");
            info.ArgumentList.Add(budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // A runner started from inside a Claude Code session must not hand that session's state to the agent.
        foreach (var name in info.Environment.Keys.Where(x => x == "CLAUDECODE" || x.StartsWith("CLAUDE_CODE_") || x.StartsWith("CLAUDE_AGENT_SDK_")).ToList())
        {
            info.Environment.Remove(name);
        }

        var process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Close();
        return new ClaudeAgent(process, new StreamWriter(logPath, append: true) { AutoFlush = true });
    }

    /// <summary>The boards MCP server for the agent. The API key stays in the environment: Claude Code expands ${VAR}.</summary>
    private static string McpConfig(RunnerConfig config)
    {
        var server = new Dictionary<string, object>
        {
            ["type"] = "http",
            ["url"] = config.McpUrl,
            ["headers"] = new Dictionary<string, string> { ["X-Api-Key"] = $"${{{config.ApiKeyEnv}}}" },
        };
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["mcpServers"] = new Dictionary<string, object> { ["boards"] = server } });
    }

    private static string Prompt(string spaceKey, AgentRunSummary run, Worktree worktree)
    {
        return $"""
            Use the boards-agent:work-on-issue skill to work on this Laraue Boards issue.
            - Issue: {run.IssueKey} in space {spaceKey}
            - Agent run id: {run.Id}
            - Branch: {worktree.Branch}, started from {worktree.BaseRef}
            """;
    }

    public Task WaitForExitAsync()
    {
        return Task.WhenAll(_process.WaitForExitAsync(), _pump);
    }

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.WaitForExit();
    }

    /// <summary>What Claude Code's final result says: how it ended, turns and cost.</summary>
    public string Summary()
    {
        if (_resultLine is null)
        {
            return "No result from Claude Code.";
        }

        try
        {
            using var result = JsonDocument.Parse(_resultLine);
            var root = result.RootElement;
            var subtype = root.TryGetProperty("subtype", out var s) ? s.GetString() : null;
            var turns = root.TryGetProperty("num_turns", out var t) ? t.GetInt32() : 0;
            var cost = root.TryGetProperty("total_cost_usd", out var c) ? c.GetDecimal() : 0;
            var isError = root.TryGetProperty("is_error", out var e) && e.GetBoolean();
            var text = root.TryGetProperty("result", out var r) ? r.GetString() : null;
            var summary = $"Claude Code: {subtype}{(isError ? " (error)" : "")}, {turns} turns, ${cost:0.00}.";
            return isError && !string.IsNullOrEmpty(text) ? $"{summary} {text}" : summary;
        }
        catch (JsonException)
        {
            return "Claude Code's result couldn't be read.";
        }
    }

    private async Task Pump()
    {
        var stderr = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                lock (_log)
                {
                    _log.WriteLine(JsonSerializer.Serialize(new { type = "stderr", text = line }));
                }
            }
        });

        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            lock (_log)
            {
                _log.WriteLine(line);
            }

            if (IsResult(line))
            {
                _resultLine = line;
            }
        }

        await stderr;
    }

    private static bool IsResult(string line)
    {
        try
        {
            using var message = JsonDocument.Parse(line);
            return message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "result";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Kill();
        _pump.Wait(TimeSpan.FromSeconds(5));
        _log.Dispose();
        _process.Dispose();
    }
}

static class RunnerVersion
{
    /// <summary>The version the release was built with (-p:Version), without the source revision suffix.</summary>
    public static readonly string Value =
        (typeof(RunnerVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev")
        .Split('+')[0];
}

/// <summary>The work-on-issue skill, embedded in the runner as a Claude Code plugin and written out (again) on start.</summary>
static class AgentPlugin
{
    private const string Prefix = "plugin/";

    public static readonly Lazy<string> Directory = new(Extract);

    private static string Extract()
    {
        var assembly = typeof(AgentPlugin).Assembly;
        var target = Path.Combine(Path.GetTempPath(), $"boards-agent-plugin-{RunnerVersion.Value}");
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var relative = name.Replace('\\', '/');
            if (!relative.StartsWith(Prefix))
            {
                continue;
            }

            var path = Path.Combine(target, relative[Prefix.Length..]);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var resource = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(path);
            resource.CopyTo(file);
        }

        return File.Exists(Path.Combine(target, ".claude-plugin", "plugin.json"))
            ? target
            : throw new InvalidOperationException("The agent skill isn't embedded in this build of boards-agent.");
    }
}

static class ClaudeExecutable
{
    /// <summary>The configured path; for plain "claude", PATH first, then the Claude desktop app's newest bundled copy.</summary>
    public static string Resolve(string configured)
    {
        if (configured != "claude")
        {
            return Paths.Expand(configured);
        }

        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(x => Path.Combine(x, "claude"))
            .FirstOrDefault(File.Exists);
        if (onPath is not null)
        {
            return onPath;
        }

        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/Claude/claude-code");
        var newest = Directory.Exists(bundled)
            ? Directory.GetDirectories(bundled)
                .Select(x => (Dir: x, Version: Version.TryParse(Path.GetFileName(x), out var v) ? v : null))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .Select(x => Directory.GetFiles(x.Dir, "claude", SearchOption.AllDirectories).FirstOrDefault(f => f.EndsWith("Contents/MacOS/claude")))
                .FirstOrDefault(x => x is not null)
            : null;
        return newest ?? throw new FileNotFoundException("Claude Code wasn't found: put claude on PATH or set claudePath in the config.");
    }
}

sealed record Worktree(string Path, string Branch, string BaseRef);

static class Git
{
    /// <summary>
    /// A worktree on branch agent/KEY under the worktree folder. Reuses the worktree, or the branch, left by an
    /// earlier run of the same issue.
    /// </summary>
    public static Worktree CreateWorktree(string repo, string baseRef, string worktreeFolder, string issueKey)
    {
        var branch = $"agent/{issueKey}";
        var path = System.IO.Path.Combine(worktreeFolder, issueKey);
        if (Directory.Exists(path))
        {
            return new Worktree(path, branch, baseRef);
        }

        Directory.CreateDirectory(worktreeFolder);
        var branchExists = Run(repo, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], throwOnError: false).ExitCode == 0;
        if (branchExists)
        {
            Run(repo, ["worktree", "add", path, branch]);
        }
        else
        {
            Run(repo, ["worktree", "add", "-b", branch, path, baseRef]);
        }

        return new Worktree(path, branch, baseRef);
    }

    private static ProcessResult Run(string workingDirectory, string[] arguments, bool throwOnError = true)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (throwOnError && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Trim()}");
        }

        return new ProcessResult(process.ExitCode, output);
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}

/// <summary>Command-line options.</summary>
sealed record RunnerArgs(string ConfigPath, bool Once, bool DryRun, bool Check, bool Version)
{
    public static RunnerArgs Parse(string[] args)
    {
        var configPath = "boards-agent.json";
        var once = false;
        var dryRun = false;
        var check = false;
        var version = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--once":
                    once = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--check":
                    check = true;
                    break;
                case "--version":
                    version = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}. Usage: boards-agent [--config <file>] [--once] [--dry-run] [--check] [--version]");
            }
        }

        return new RunnerArgs(configPath, once, dryRun, check, version);
    }
}

/// <summary>The runner's settings, read from a JSON file. The API key itself is never stored there.</summary>
sealed record RunnerConfig
{
    /// <summary>The Boards MCP endpoint.</summary>
    public string McpUrl { get; init; } = "http://localhost:5202/mcp";

    /// <summary>The environment variable holding the API key of the user the runner works for.</summary>
    public string ApiKeyEnv { get; init; } = "BOARDS_AGENT_API_KEY";

    /// <summary>Space key -> the local repository its issues are worked on in. Only these spaces are polled.</summary>
    public Dictionary<string, SpaceConfig> Spaces { get; init; } = [];

    /// <summary>Where the runner creates a git worktree per issue.</summary>
    public string WorktreeFolder { get; init; } = "~/boards-agent-worktrees";

    /// <summary>
    /// The Claude Code executable. "claude" means the one on PATH, or else the newest one bundled with the Claude
    /// desktop app.
    /// </summary>
    public string ClaudePath { get; init; } = "claude";

    /// <summary>
    /// What Claude Code may do without asking, besides editing files in the worktree (--allowedTools). Anything else
    /// is denied, since nobody is there to approve it.
    /// </summary>
    public string[] AllowedTools { get; init; } =
    [
        "Bash(dotnet build:*)",
        "Bash(dotnet test:*)",
        "Bash(dotnet restore:*)",
        "Bash(git status:*)",
        "Bash(git diff:*)",
        "Bash(git log:*)",
        "Bash(git show:*)",
        "Bash(git add:*)",
        "Bash(git commit:*)",
    ];

    /// <summary>Claude Code's --max-turns.</summary>
    public int MaxTurns { get; init; } = 50;

    /// <summary>Claude Code's --max-budget-usd, if the installed version supports it.</summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>A run taking longer than this is stopped and failed.</summary>
    public int TimeoutMinutes { get; init; } = 60;

    /// <summary>How often a working run is checked for a cancel and the timeout.</summary>
    public int CancelCheckSeconds { get; init; } = 30;

    /// <summary>How often Ready runs are polled.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    public static RunnerConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file {path} not found. Copy boards-agent.example.json to {path} and edit it.");
        }

        var config = JsonSerializer.Deserialize<RunnerConfig>(File.ReadAllText(path), Json.Options)
            ?? throw new InvalidOperationException($"{path} is empty.");
        if (config.Spaces.Count == 0)
        {
            throw new InvalidOperationException($"{path}: map at least one space key to a local repository in \"spaces\".");
        }

        return config with
        {
            Spaces = config.Spaces.ToDictionary(x => x.Key, x => x.Value with { Repo = Paths.Expand(x.Value.Repo) }),
            WorktreeFolder = Paths.Expand(config.WorktreeFolder),
        };
    }
}

/// <summary>One space's repository.</summary>
sealed record SpaceConfig
{
    /// <summary>The local checkout. Worktrees are created from it, the checkout itself isn't touched.</summary>
    public required string Repo { get; init; }

    /// <summary>What an issue's branch starts from, e.g. origin/main.</summary>
    public string BaseRef { get; init; } = "HEAD";
}

/// <summary>The Boards MCP tools the runner calls.</summary>
sealed class BoardsClient(McpClient client) : IAsyncDisposable
{
    private const int PageSize = 50;

    public static async Task<BoardsClient> ConnectAsync(string url, string apiKey, CancellationToken ct)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(url),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
                Name = "boards-agent",
            });

        return new BoardsClient(await McpClient.CreateAsync(transport, cancellationToken: ct));
    }

    public Task<MeInfo> GetMe(CancellationToken ct)
    {
        return Call<MeInfo>("get_me", new Dictionary<string, object?>(), ct);
    }

    /// <summary>The user's own Ready runs in a space that aren't waiting for a blocker, oldest first.</summary>
    public async Task<IReadOnlyList<AgentRunSummary>> ListReadyRuns(string spaceKey, Guid userId, CancellationToken ct)
    {
        var result = new List<AgentRunSummary>();
        for (var page = 0; ; page++)
        {
            var runs = await Call<AgentRunListPage>(
                "list_agent_runs",
                new Dictionary<string, object?>
                {
                    ["spaceKey"] = spaceKey,
                    ["state"] = "Ready",
                    ["assigneeIds"] = new[] { userId },
                    ["excludeBlocked"] = true,
                    ["page"] = page,
                    ["count"] = PageSize,
                },
                ct);
            result.AddRange(runs.Runs);
            if (!runs.HasNextPage)
            {
                return result;
            }
        }
    }

    /// <summary>False when the run was taken, cancelled or is blocked. Throws (403) when the issue isn't the user's.</summary>
    public Task<bool> ClaimAgentRun(long runId, CancellationToken ct)
    {
        return Call<bool>("claim_agent_run", new Dictionary<string, object?> { ["runId"] = runId }, ct);
    }

    /// <summary>False when the run isn't Working anymore (cancelled meanwhile).</summary>
    public Task<bool> FinishAgentRun(long runId, string state, string? reason, CancellationToken ct)
    {
        return Call<bool>(
            "finish_agent_run",
            new Dictionary<string, object?> { ["runId"] = runId, ["state"] = state, ["reason"] = reason },
            ct);
    }

    public Task<IReadOnlyList<EpicStatusSummary>> ListStatuses(string spaceKey, CancellationToken ct)
    {
        return Call<IReadOnlyList<EpicStatusSummary>>("list_statuses", new Dictionary<string, object?> { ["spaceKey"] = spaceKey }, ct);
    }

    public Task<IssueDetail> GetIssue(string issueKey, CancellationToken ct)
    {
        return Call<IssueDetail>("get_issue", new Dictionary<string, object?> { ["issueKey"] = issueKey }, ct);
    }

    public Task EditIssueStatus(string issueKey, long statusId, string? comment, CancellationToken ct)
    {
        return CallVoid(
            "edit_issue_status",
            new Dictionary<string, object?> { ["issueKey"] = issueKey, ["statusId"] = statusId, ["comment"] = comment },
            ct);
    }

    public Task CreateComment(string issueKey, string text, CancellationToken ct)
    {
        return CallVoid("create_comment", new Dictionary<string, object?> { ["issueKey"] = issueKey, ["text"] = text }, ct);
    }

    private async Task<T> Call<T>(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var text = await CallVoid(tool, arguments, ct);
        return JsonSerializer.Deserialize<T>(text, Json.Options)
            ?? throw new BoardsToolException(tool, "empty result");
    }

    private async Task<string> CallVoid(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: ct);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(x => x.Text));
        if (result.IsError == true)
        {
            throw new BoardsToolException(tool, text);
        }

        return text;
    }

    public ValueTask DisposeAsync()
    {
        return client.DisposeAsync();
    }
}

sealed class BoardsToolException(string tool, string message) : Exception($"{tool}: {message}");

sealed record MeInfo(MeUser User, MeOrganization Organization);

sealed record MeUser(Guid Id, string DisplayName);

sealed record MeOrganization(long Id, string Name, bool IsPersonal);

sealed record AgentRunListPage(IReadOnlyList<AgentRunSummary> Runs, long Page, bool HasNextPage);

sealed record AgentRunSummary(
    long Id,
    string IssueKey,
    string IssueTitle,
    string IssueUrl,
    string State,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime? ClaimedAt,
    string? ClaimedByApiKey,
    DateTime? FinishedAt,
    string? FailureReason,
    bool IsBlocked);

sealed record StatusSummary(long Id, string Name, int CategoryId, string Category);

sealed record EpicStatusSummary(long EpicId, string EpicName, IReadOnlyList<StatusSummary> Statuses);

sealed record IssueDetail(string Key, string Title, string Status, long? StatusId, long? EpicId, int StatusCategoryId, AgentRunSummary? LatestAgentRun);

static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

static class Paths
{
    public static string Expand(string path)
    {
        return path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/'))
            : Path.GetFullPath(path);
    }
}

static class Log
{
    public static void Info(string message)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
    }

    public static void Error(string message)
    {
        Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} ERROR {message}");
    }
}
