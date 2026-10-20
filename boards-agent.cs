#!/usr/bin/env dotnet
// boards-agent: picks up Laraue Boards issues handed to the agent and works on them with Claude Code.
// Run: dotnet run boards-agent.cs -- [setup] [--config <file>] [--once] [--dry-run] [--check] [--version]
#:package ModelContextProtocol.Core@1.4.0
#:property PublishAot=false
#:property PublishTrimmed=true
#:property JsonSerializerIsReflectionEnabledByDefault=false

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var options = RunnerArgs.Parse(args);
if (options.LogPath is not null)
{
    Log.ToFile(options.LogPath);
}
if (options.Version)
{
    Console.WriteLine($"boards-agent {RunnerVersion.Value}");
    return 0;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

// launchd/systemd stop the runner with SIGTERM: stop like Ctrl+C, so a run in progress is failed, not left Working.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        stop.Cancel();
    });

if (options.Command == RunnerArgs.SetupCommand)
{
    try
    {
        return await Setup.RunAsync(options, stop.Token);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        return 1;
    }
}

if (options.Command == RunnerArgs.ServiceCommand)
{
    return Service.Run(options);
}

var config = RunnerConfig.Load(options.ConfigPath);
if (options.Check)
{
    Console.WriteLine($"boards-agent {RunnerVersion.Value}");
    Console.WriteLine($"Config: {options.ConfigPath}");
    Console.WriteLine($"Claude Code: {ClaudeExecutable.Resolve(config.ClaudePath)}");
    Console.WriteLine($"Agent skill: {AgentPlugin.Directory.Value}");
}

var apiKey = ApiKeySource.Resolve(config);
if (apiKey is null)
{
    Log.Error("No Boards API key: run `boards-agent setup` to sign in.");
    return 1;
}

await using var boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey, stop.Token);

var me = await boards.GetMe(stop.Token);
Log.Info($"Connected to {config.McpUrl} as {me.User.DisplayName} ({me.User.Id}), organization {me.Organization.Name}.");
if (options.Check)
{
    return 0;
}

Log.Info($"Watching spaces: {string.Join(", ", config.Spaces.Keys)}.{(options.DryRun ? " Dry run - nothing is claimed." : $" Claude Code: {ClaudeExecutable.Resolve(config.ClaudePath)}")}");

var runner = new Runner(boards, config, apiKey);
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
sealed class Runner(BoardsClient boards, RunnerConfig config, string apiKey)
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
        using var agent = ClaudeAgent.Start(config, apiKey, spaceKey, run, worktree, logPath);
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

    public static ClaudeAgent Start(RunnerConfig config, string apiKey, string spaceKey, AgentRunSummary run, Worktree worktree, string logPath)
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

        ClaudeExecutable.RemoveSessionEnvironment(info);

        // The MCP config refers to the key as ${VAR}, so it reaches Claude Code only through its environment.
        info.Environment[config.ApiKeyEnv] = apiKey;

        var process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Close();
        return new ClaudeAgent(process, new StreamWriter(logPath, append: true) { AutoFlush = true });
    }

    /// <summary>The boards MCP server for the agent. The API key stays in the environment: Claude Code expands ${VAR}.</summary>
    private static string McpConfig(RunnerConfig config)
    {
        var server = new JsonObject
        {
            ["type"] = "http",
            ["url"] = config.McpUrl,
            ["headers"] = new JsonObject { ["X-Api-Key"] = $"${{{config.ApiKeyEnv}}}" },
        };
        return new JsonObject { ["mcpServers"] = new JsonObject { ["boards"] = server } }.ToJsonString();
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
                    _log.WriteLine(new JsonObject { ["type"] = "stderr", ["text"] = line }.ToJsonString());
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
    /// <summary>
    /// The configured path; for plain "claude", PATH (and ~/.local/bin, where the native installer puts it) first, then the
    /// Claude desktop app's newest bundled copy.
    /// </summary>
    public static string Resolve(string configured)
    {
        if (configured != "claude")
        {
            return Paths.Expand(configured);
        }

        // On Windows, the native claude.exe before npm's claude.cmd shim, which goes through cmd.exe's argument quoting.
        string[] names = OperatingSystem.IsWindows() ? ["claude.exe", "claude.cmd"] : ["claude"];
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .ToList();
        var onPath = names
            .SelectMany(name => folders.Select(folder => Path.Combine(folder, name)))
            .FirstOrDefault(File.Exists);
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

    /// <summary>A runner started from inside a Claude Code session must not hand that session's state to the Claude it starts.</summary>
    public static void RemoveSessionEnvironment(System.Diagnostics.ProcessStartInfo info)
    {
        foreach (var name in info.Environment.Keys.Where(x => x == "CLAUDECODE" || x.StartsWith("CLAUDE_CODE_") || x.StartsWith("CLAUDE_AGENT_SDK_")).ToList())
        {
            info.Environment.Remove(name);
        }
    }

    /// <summary>Whether Claude Code is signed in, by `claude auth status`.</summary>
    public static bool IsSignedIn(string executable)
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
    public static void SignIn(string executable)
    {
        var info = new System.Diagnostics.ProcessStartInfo(executable);
        info.ArgumentList.Add("auth");
        info.ArgumentList.Add("login");
        RemoveSessionEnvironment(info);
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
    }
}

sealed record Worktree(string Path, string Branch, string BaseRef);

static class Git
{
    /// <summary>
    /// A worktree on branch agent/KEY under the worktree folder. Reuses the worktree, or the branch, left by an
    /// earlier run of the same issue. A new branch starts from <paramref name="baseRef"/> fetched just before, so
    /// e.g. origin/main isn't however old the checkout's last fetch was.
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
            FetchRemoteRef(repo, baseRef);
            Run(repo, ["worktree", "add", "-b", branch, path, baseRef]);
        }

        return new Worktree(path, branch, baseRef);
    }

    /// <summary>
    /// Fetches <paramref name="baseRef"/> when it's a remote branch (e.g. origin/main). A failed fetch (offline, no access)
    /// isn't fatal: the work starts from the ref as it is.
    /// </summary>
    private static void FetchRemoteRef(string repo, string baseRef)
    {
        var slash = baseRef.IndexOf('/');
        if (slash <= 0)
        {
            return;
        }

        var remote = baseRef[..slash];
        var remotes = Run(repo, ["remote"], throwOnError: false).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!remotes.Contains(remote))
        {
            return;
        }

        var fetch = Run(repo, ["fetch", "--quiet", remote, baseRef[(slash + 1)..]], throwOnError: false);
        if (fetch.ExitCode != 0)
        {
            Log.Error($"git fetch {remote} {baseRef[(slash + 1)..]} failed, starting from {baseRef} as it is: {fetch.Error.Trim()}");
        }
    }

    /// <summary>The top folder of the git checkout <paramref name="folder"/> is in, or null when it isn't in one.</summary>
    public static string? FindCheckout(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var result = Run(folder, ["rev-parse", "--show-toplevel"], throwOnError: false);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    /// <summary>What new branches should start from: the remote's default branch (e.g. origin/main), else HEAD.</summary>
    public static string DefaultBaseRef(string repo)
    {
        var result = Run(repo, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], throwOnError: false);
        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } remoteHead ? remoteHead : "HEAD";
    }

    private static ProcessResult Run(string workingDirectory, string[] arguments, bool throwOnError = true)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
        // In the background service nobody can answer a credentials prompt: fail instead of waiting for one.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var result = Shell.Run(info);
        if (throwOnError && result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error.Trim()}");
        }

        return result;
    }
}

sealed record ProcessResult(int ExitCode, string Output, string Error);

static class Shell
{
    /// <summary>Runs a process to the end, capturing its output; <paramref name="input"/> goes to its stdin.</summary>
    public static ProcessResult Run(System.Diagnostics.ProcessStartInfo info, string? input = null)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        using var process = System.Diagnostics.Process.Start(info)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
        }

        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error.Result);
    }

    public static ProcessResult Run(string file, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Run(info);
    }
}

/// <summary>
/// Command-line options. <see cref="WebUrl"/>, <see cref="ApiUrl"/> and <see cref="McpUrl"/> point setup at another
/// Boards than production, e.g. a local one.
/// </summary>
sealed record RunnerArgs(
    string? Command,
    string ConfigPath,
    bool Once,
    bool DryRun,
    bool Check,
    bool Version,
    string? WebUrl,
    string? ApiUrl,
    string? McpUrl,
    bool NoBrowser,
    string? ServiceAction,
    string? LogPath)
{
    public const string SetupCommand = "setup";

    public const string ServiceCommand = "service";

    private const string Usage =
        "Usage: boards-agent [setup] [--config <file>] [--log <file>] [--once] [--dry-run] [--check] [--version]\n" +
        "       boards-agent setup [--no-browser] [--web-url <url>] [--api-url <url>] [--mcp-url <url>]\n" +
        "       boards-agent service install|uninstall|status [--config <file>]";

    public static RunnerArgs Parse(string[] args)
    {
        string? command = null;
        var configPath = RunnerConfig.DefaultPath;
        var once = false;
        var dryRun = false;
        var check = false;
        var version = false;
        string? webUrl = null;
        string? apiUrl = null;
        string? mcpUrl = null;
        var noBrowser = false;
        string? serviceAction = null;
        string? logPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case SetupCommand when i == 0:
                    command = SetupCommand;
                    break;
                case ServiceCommand when i == 0 && i + 1 < args.Length:
                    command = ServiceCommand;
                    serviceAction = args[++i];
                    break;
                case "--config" when i + 1 < args.Length:
                    configPath = Paths.Expand(args[++i]);
                    break;
                case "--web-url" when i + 1 < args.Length:
                    webUrl = args[++i];
                    break;
                case "--api-url" when i + 1 < args.Length:
                    apiUrl = args[++i];
                    break;
                case "--mcp-url" when i + 1 < args.Length:
                    mcpUrl = args[++i];
                    break;
                case "--log" when i + 1 < args.Length:
                    logPath = Paths.Expand(args[++i]);
                    break;
                case "--no-browser":
                    noBrowser = true;
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
                    throw new ArgumentException($"Unknown argument: {args[i]}.\n{Usage}");
            }
        }

        if (command == ServiceCommand && serviceAction is not ("install" or "uninstall" or "status"))
        {
            throw new ArgumentException($"Unknown service action: {serviceAction}.\n{Usage}");
        }

        return new RunnerArgs(command, configPath, once, dryRun, check, version, webUrl, apiUrl, mcpUrl, noBrowser, serviceAction, logPath);
    }
}

/// <summary>
/// The runner's settings, written by `boards-agent setup` to <see cref="DefaultPath"/>. The API key itself is never
/// stored there - it's in the OS credential store (<see cref="CredentialStore"/>).
/// </summary>
sealed record RunnerConfig
{
    /// <summary>The Boards web app, where setup sends the person to allow the sign-in.</summary>
    public string WebUrl { get; init; } = "https://boards.laraue.com";

    /// <summary>The Boards web API, which issues the API key at setup.</summary>
    public string ApiUrl { get; init; } = "https://boards.laraue.com/boards-api";

    /// <summary>The Boards MCP endpoint.</summary>
    public string McpUrl { get; init; } = "https://boards.laraue.com/boards-mcp/mcp";

    /// <summary>The organization the API key belongs to - its credential store entry is named after it.</summary>
    public long? OrganizationId { get; init; }

    public string? OrganizationName { get; init; }

    /// <summary>
    /// An environment variable that, when set, overrides the stored key (e.g. on a machine without a credential
    /// store). Also how Claude Code receives the key: the runner sets it for the Claude it starts.
    /// </summary>
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

    /// <summary>~/.config/boards-agent/config.json ($XDG_CONFIG_HOME if set), or %APPDATA%\\boards-agent\\config.json on Windows.</summary>
    public static string DefaultPath
    {
        get
        {
            var folder = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(folder, "boards-agent", "config.json");
        }
    }

    public static RunnerConfig Load(string path)
    {
        var config = TryLoad(path)
            ?? throw new FileNotFoundException($"No config at {path}. Run `boards-agent setup` first.");
        if (config.Spaces.Count == 0)
        {
            throw new InvalidOperationException($"{path}: no space is mapped to a local repository. Run `boards-agent setup`.");
        }

        return config;
    }

    /// <summary>The config at <paramref name="path"/> with its paths expanded, or null when there's none.</summary>
    public static RunnerConfig? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var config = JsonSerializer.Deserialize(File.ReadAllText(path), RunnerJson.Default.RunnerConfig)
            ?? throw new InvalidOperationException($"{path} is empty.");
        return config with
        {
            Spaces = config.Spaces.ToDictionary(x => x.Key, x => x.Value with { Repo = Paths.Expand(x.Value.Repo) }),
            WorktreeFolder = Paths.Expand(config.WorktreeFolder),
        };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, RunnerJson.Default.RunnerConfig));
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
        return Call("get_me", new Dictionary<string, object?>(), RunnerJson.Default.MeInfo, ct);
    }

    public Task<IReadOnlyList<SpaceSummary>> ListSpaces(CancellationToken ct)
    {
        return Call("list_spaces", new Dictionary<string, object?>(), RunnerJson.Default.IReadOnlyListSpaceSummary, ct);
    }

    /// <summary>The user's own Ready runs in a space that aren't waiting for a blocker, oldest first.</summary>
    public async Task<IReadOnlyList<AgentRunSummary>> ListReadyRuns(string spaceKey, Guid userId, CancellationToken ct)
    {
        var result = new List<AgentRunSummary>();
        for (var page = 0; ; page++)
        {
            var runs = await Call(
                "list_agent_runs",
                new Dictionary<string, object?>
                {
                    ["spaceKey"] = spaceKey,
                    ["state"] = "Ready",
                    // The SDK serializes arguments with its own JSON context, which has string[] but not Guid[].
                    ["assigneeIds"] = new[] { userId.ToString() },
                    ["excludeBlocked"] = true,
                    ["page"] = page,
                    ["count"] = PageSize,
                },
                RunnerJson.Default.AgentRunListPage,
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
        return Call("claim_agent_run", new Dictionary<string, object?> { ["runId"] = runId }, RunnerJson.Default.Boolean, ct);
    }

    /// <summary>False when the run isn't Working anymore (cancelled meanwhile).</summary>
    public Task<bool> FinishAgentRun(long runId, string state, string? reason, CancellationToken ct)
    {
        return Call(
            "finish_agent_run",
            new Dictionary<string, object?> { ["runId"] = runId, ["state"] = state, ["reason"] = reason },
            RunnerJson.Default.Boolean,
            ct);
    }

    public Task<IReadOnlyList<EpicStatusSummary>> ListStatuses(string spaceKey, CancellationToken ct)
    {
        return Call("list_statuses", new Dictionary<string, object?> { ["spaceKey"] = spaceKey }, RunnerJson.Default.IReadOnlyListEpicStatusSummary, ct);
    }

    public Task<IssueDetail> GetIssue(string issueKey, CancellationToken ct)
    {
        return Call("get_issue", new Dictionary<string, object?> { ["issueKey"] = issueKey }, RunnerJson.Default.IssueDetail, ct);
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

    private async Task<T> Call<T>(string tool, IReadOnlyDictionary<string, object?> arguments, JsonTypeInfo<T> result, CancellationToken ct)
    {
        var text = await CallVoid(tool, arguments, ct);
        return JsonSerializer.Deserialize(text, result)
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

sealed record SpaceSummary(string Key, string Name, bool CanCreateIssue);

/// <summary>`claude auth status`'s answer - only what setup reads.</summary>
sealed record ClaudeAuthStatus(bool LoggedIn);

sealed record EpicStatusSummary(long EpicId, string EpicName, IReadOnlyList<StatusSummary> Statuses);

sealed record IssueDetail(string Key, string Title, string Status, long? StatusId, long? EpicId, int StatusCategoryId, AgentRunSummary? LatestAgentRun);

/// <summary>Source-generated JSON for everything the runner reads, so a trimmed build keeps working.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunnerConfig))]
[JsonSerializable(typeof(IReadOnlyList<SpaceSummary>))]
[JsonSerializable(typeof(ClaudeAuthStatus))]
[JsonSerializable(typeof(AgentSignInStart))]
[JsonSerializable(typeof(AgentSignInPoll))]
[JsonSerializable(typeof(MeInfo))]
[JsonSerializable(typeof(AgentRunListPage))]
[JsonSerializable(typeof(IReadOnlyList<EpicStatusSummary>))]
[JsonSerializable(typeof(IssueDetail))]
[JsonSerializable(typeof(bool))]
partial class RunnerJson : JsonSerializerContext;

/// <summary>
/// `boards-agent service`: runs the runner in the background as the signed-in user - where their credential store and
/// Claude Code sign-in are - started at login and restarted if it stops. A launchd LaunchAgent on macOS, a Task Scheduler
/// logon task on Windows. systemd isn't supported yet.
/// </summary>
static class Service
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
            Console.WriteLine("The background service isn't supported on this system yet. Run `boards-agent` in a terminal instead.");
            return 1;
        }

        return options.ServiceAction switch
        {
            "install" => Install(options.ConfigPath),
            "uninstall" => Uninstall(),
            _ => Status(),
        };
    }

    public static int Install(string configPath)
    {
        var executable = StableExecutablePath();
        if (executable is null)
        {
            Console.WriteLine("The service needs the installed boards-agent binary, not `dotnet run`.");
            return 1;
        }

        var config = RunnerConfig.TryLoad(configPath);
        if (config is null || config.Spaces.Count == 0 || ApiKeySource.Resolve(config) is null)
        {
            Console.WriteLine("Run `boards-agent setup` first: the service needs a signed-in config with a mapped space.");
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
        Console.WriteLine("Hand an issue to the agent in Boards. `boards-agent service status` shows what it's doing.");
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

    private static int Status()
    {
        if (!IsInstalled)
        {
            Console.WriteLine("The background service isn't installed. `boards-agent service install` installs it.");
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
    private static string? StableExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (path is null || Path.GetFileNameWithoutExtension(path) == "dotnet")
        {
            return null;
        }

        var cellar = path.IndexOf("/Cellar/boards-agent/", StringComparison.Ordinal);
        if (cellar > 0)
        {
            var linked = Path.Combine(path[..cellar], "bin", "boards-agent");
            if (File.Exists(linked))
            {
                return linked;
            }
        }

        return path;
    }

    private static string? InstallLaunchAgent(string executable, string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, Plist(executable, configPath));

        Shell.Run("/bin/launchctl", "bootout", $"{Domain}/{Label}");
        var bootstrap = Shell.Run("/bin/launchctl", "bootstrap", Domain, PlistPath);
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
                <Description>boards-agent: works on Laraue Boards issues handed to the agent.</Description>
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

/// <summary>Where the runner's API key comes from: the environment variable when set, else the credential store.</summary>
static class ApiKeySource
{
    public static string? Resolve(RunnerConfig config)
    {
        if (Environment.GetEnvironmentVariable(config.ApiKeyEnv) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        return config.OrganizationId is { } organizationId
            ? CredentialStore.Load(CredentialStore.Account(organizationId))
            : null;
    }
}

/// <summary>
/// The API key in the OS credential store: the macOS Keychain (security), the Secret Service on Linux (secret-tool), or
/// the Windows Credential Manager.
/// One entry per organization. The key never goes on a command line - it's passed on stdin - and never to a file.
/// </summary>
static class CredentialStore
{
    private const string Service = "boards-agent";

    public static string Account(long organizationId)
    {
        return $"organization-{organizationId}";
    }

    public static void Save(string account, string label, string secret)
    {
        ProcessResult result;
        if (OperatingSystem.IsMacOS())
        {
            // security's interactive mode reads the command from stdin, so the key isn't visible in the process list.
            // -A lets the runner read it back after an upgrade changes its binary, without a Keychain prompt.
            var safeLabel = label.Replace("\"", "").Replace("\\", "");
            result = Shell.Run(
                new System.Diagnostics.ProcessStartInfo("/usr/bin/security", "-i"),
                $"add-generic-password -U -A -s {Service} -a {account} -l \"{safeLabel}\" -w {secret}\n");
        }
        else if (OperatingSystem.IsLinux())
        {
            var info = new System.Diagnostics.ProcessStartInfo("secret-tool");
            foreach (var argument in new[] { "store", $"--label={label}", "service", Service, "account", account })
            {
                info.ArgumentList.Add(argument);
            }

            result = Shell.Run(info, secret);
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsCredentials.Write($"{Service}:{account}", label, secret);
            return;
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Storing the key isn't supported on this system yet. Set the BOARDS_AGENT_API_KEY environment variable instead.");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Couldn't save the API key in the credential store: {result.Error.Trim()}");
        }
    }

    /// <summary>The stored key, or null when there's none (or no credential store).</summary>
    public static string? Load(string account)
    {
        ProcessResult result;
        if (OperatingSystem.IsMacOS())
        {
            result = Shell.Run("/usr/bin/security", "find-generic-password", "-s", Service, "-a", account, "-w");
        }
        else if (OperatingSystem.IsLinux())
        {
            result = Shell.Run("secret-tool", "lookup", "service", Service, "account", account);
        }
        else if (OperatingSystem.IsWindows())
        {
            return WindowsCredentials.Read($"{Service}:{account}");
        }
        else
        {
            return null;
        }

        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } secret ? secret : null;
    }
}

/// <summary>A generic credential in the Windows Credential Manager, through advapi32's CredWrite/CredRead.</summary>
static class WindowsCredentials
{
    private const int GenericType = 1;

    private const int PersistLocalMachine = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public int LastWrittenLow;
        public int LastWrittenHigh;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Write(string target, string label, string secret)
    {
        var blob = System.Text.Encoding.UTF8.GetBytes(secret);
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(blob.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(blob, 0, buffer, blob.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = target,
                Comment = label,
                CredentialBlobSize = blob.Length,
                CredentialBlob = buffer,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException(
                    $"Couldn't save the API key in the Credential Manager (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? Read(string target)
    {
        if (!CredRead(target, GenericType, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = System.Runtime.InteropServices.Marshal.PtrToStructure<Credential>(pointer);
            var blob = new byte[credential.CredentialBlobSize];
            System.Runtime.InteropServices.Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return System.Text.Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(pointer);
        }
    }
}

sealed record AgentSignInStart(string DeviceCode, string UserCode, string VerificationPath, DateTime ExpiresAt, int IntervalSeconds);

sealed record AgentSignInPoll(string Outcome, string? ApiKey, long? OrganizationId, string? OrganizationName, Guid? UserId);

sealed record SignedIn(string ApiKey, long OrganizationId, string OrganizationName);

/// <summary>
/// "Connect this machine": the runner asks Boards for a sign-in, the person allows it in the browser for one of their
/// organizations, and the runner polls until Boards hands it the organization's new API key.
/// </summary>
static class BoardsSignIn
{
    /// <summary>How the runner introduces itself: Boards shows it on the Allow page and names the key "&lt;this&gt; (&lt;machine&gt;)".</summary>
    private const string ClientName = "Claude Code agent";

    /// <summary>Boards allows 37 characters of machine name, so the key name fits.</summary>
    private const int MaxMachineNameLength = 37;

    /// <param name="openBrowser">False prints the link only, e.g. over SSH, to open it on another device.</param>
    /// <param name="replacesApiKey">
    /// The key this machine held so far: Boards revokes exactly that key once the new one is issued (only if it's the
    /// same user's, in the same organization), instead of leaving it active.
    /// </param>
    public static async Task<SignedIn?> RunAsync(RunnerConfig config, bool openBrowser, string? replacesApiKey, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.ApiUrl.TrimEnd('/') + "/") };

        var start = await PostAsync(
            http,
            "api/agent-sign-in",
            new JsonObject { ["clientName"] = ClientName, ["machineName"] = MachineName(), ["replacesApiKey"] = replacesApiKey },
            RunnerJson.Default.AgentSignInStart,
            ct);

        var url = config.WebUrl.TrimEnd('/') + start.VerificationPath;
        Console.WriteLine();
        if (openBrowser)
        {
            Console.WriteLine($"Opening Boards in your browser to connect this machine. Your code: {start.UserCode}");
            Console.WriteLine($"If the browser didn't open, go to {url}");
            OpenBrowser(url);
        }
        else
        {
            Console.WriteLine($"To connect this machine, open {url} and check that it shows the code {start.UserCode}.");
        }

        Console.WriteLine("Waiting for you to click Allow...");

        var interval = TimeSpan.FromSeconds(Math.Max(start.IntervalSeconds, 1));
        while (true)
        {
            await Task.Delay(interval, ct);
            var poll = await PostAsync(
                http,
                "api/agent-sign-in/token",
                new JsonObject { ["deviceCode"] = start.DeviceCode },
                RunnerJson.Default.AgentSignInPoll,
                ct);

            switch (poll.Outcome)
            {
                case "Approved" when poll is { ApiKey: { } apiKey, OrganizationId: { } organizationId }:
                    return new SignedIn(apiKey, organizationId, poll.OrganizationName ?? organizationId.ToString());
                case "SlowDown":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "Denied":
                    Console.WriteLine("You denied the sign-in in the browser.");
                    return null;
                case "Expired":
                    Console.WriteLine("The sign-in timed out. Run `boards-agent setup` again.");
                    return null;
            }
        }
    }

    private static string MachineName()
    {
        var name = Environment.MachineName;
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".local".Length];
        }

        return name.Length > MaxMachineNameLength ? name[..MaxMachineNameLength] : name;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // The URL is printed too; a machine without a browser (e.g. over SSH) opens it elsewhere.
        }
    }

    private static async Task<T> PostAsync<T>(HttpClient http, string path, JsonObject body, JsonTypeInfo<T> result, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Boards answered {(int)response.StatusCode} to {path}: {text}");
        }

        return JsonSerializer.Deserialize(text, result)
            ?? throw new InvalidOperationException($"Boards gave an empty answer to {path}.");
    }
}

/// <summary>
/// `boards-agent setup`: signs this machine in to Boards (the key goes to the credential store), maps spaces to local
/// repositories, and makes sure Claude Code is signed in. Writes the config; running it again keeps what's there.
/// </summary>
static class Setup
{
    public static async Task<int> RunAsync(RunnerArgs options, CancellationToken ct)
    {
        var config = RunnerConfig.TryLoad(options.ConfigPath) ?? new RunnerConfig();
        config = config with
        {
            WebUrl = options.WebUrl ?? config.WebUrl,
            ApiUrl = options.ApiUrl ?? config.ApiUrl,
            McpUrl = options.McpUrl ?? config.McpUrl,
        };

        Console.WriteLine($"boards-agent {RunnerVersion.Value} setup");

        // 1. Boards: reuse a stored key that still works, unless the person wants another organization.
        var apiKey = ApiKeySource.Resolve(config);
        MeInfo? me = apiKey is null ? null : await TryGetMe(config.McpUrl, apiKey, ct);
        if (me is not null && !Confirm($"Signed in to Boards as {me.User.DisplayName}, organization {me.Organization.Name}. Keep it?", defaultYes: true))
        {
            me = null;
        }

        if (me is null)
        {
            // Only the key this machine stored itself, never BOARDS_AGENT_API_KEY, which may be a key made for something else.
            var storedKey = config.OrganizationId is { } previousOrganizationId
                ? CredentialStore.Load(CredentialStore.Account(previousOrganizationId))
                : null;
            var signedIn = await BoardsSignIn.RunAsync(config, !options.NoBrowser, storedKey, ct);
            if (signedIn is null)
            {
                return 1;
            }

            CredentialStore.Save(
                CredentialStore.Account(signedIn.OrganizationId),
                $"Boards agent - {signedIn.OrganizationName}",
                signedIn.ApiKey);
            apiKey = signedIn.ApiKey;
            me = await TryGetMe(config.McpUrl, apiKey, ct)
                ?? throw new InvalidOperationException($"The new API key doesn't work with {config.McpUrl}.");

            // Spaces belong to an organization: another organization starts over.
            if (config.OrganizationId != signedIn.OrganizationId)
            {
                config = config with { Spaces = [] };
            }

            config = config with { OrganizationId = signedIn.OrganizationId, OrganizationName = signedIn.OrganizationName };
            Console.WriteLine($"Signed in as {me.User.DisplayName}, organization {me.Organization.Name}. The API key is in your {CredentialStoreName}.");
        }

        // 2. Which local repository each space's issues are worked on in.
        await using (var boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey!, ct))
        {
            var spaces = await boards.ListSpaces(ct);
            config = config with { Spaces = MapSpaces(spaces, config.Spaces) };
        }

        config.Save(options.ConfigPath);
        Console.WriteLine($"Saved {options.ConfigPath}.");

        // 3. Claude Code.
        string claude;
        try
        {
            claude = ClaudeExecutable.Resolve(config.ClaudePath);
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("Claude Code isn't installed. Install it (https://claude.com/claude-code), then run `boards-agent setup` again.");
            return 1;
        }

        if (!ClaudeExecutable.IsSignedIn(claude))
        {
            Console.WriteLine("Claude Code isn't signed in. Starting `claude auth login`...");
            ClaudeExecutable.SignIn(claude);
            if (!ClaudeExecutable.IsSignedIn(claude))
            {
                Console.WriteLine("Claude Code still isn't signed in. Run `boards-agent setup` again once it is.");
                return 1;
            }
        }

        Console.WriteLine($"Claude Code: {claude} (signed in).");
        Console.WriteLine();
        Console.WriteLine(config.Spaces.Count == 0
            ? "No space is mapped to a repository yet, so there's nothing to watch. Run `boards-agent setup` again to map one."
            : $"Done. Watching {string.Join(", ", config.Spaces.Keys)}.");
        if (config.Spaces.Count > 0 && Service.IsSupported)
        {
            if (Service.IsInstalled)
            {
                // The running service read the old config at start.
                Service.Restart();
                Console.WriteLine("The background service was restarted with the new settings. Hand an issue to the agent in Boards.");
            }
            else if (Confirm("Run the agent in the background, also after you restart the computer?", defaultYes: true))
            {
                return Service.Install(options.ConfigPath);
            }
            else
            {
                Console.WriteLine("Start the runner with `boards-agent`, then hand an issue to the agent in Boards.");
            }
        }
        else if (config.Spaces.Count > 0)
        {
            Console.WriteLine("Start the runner with `boards-agent`, then hand an issue to the agent in Boards.");
        }

        return 0;
    }

    private static string CredentialStoreName => OperatingSystem.IsMacOS() ? "Keychain" : "credential store";

    /// <summary>The user the key signs in as, or null when it doesn't work (revoked, or for another Boards).</summary>
    private static async Task<MeInfo?> TryGetMe(string mcpUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            await using var boards = await BoardsClient.ConnectAsync(mcpUrl, apiKey, ct);
            return await boards.GetMe(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks which space goes with which repository, starting with the git checkout setup runs in. Existing mappings stay.
    /// </summary>
    private static Dictionary<string, SpaceConfig> MapSpaces(IReadOnlyList<SpaceSummary> spaces, Dictionary<string, SpaceConfig> existing)
    {
        var mapped = existing
            .Where(x => spaces.Any(space => space.Key == x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        if (spaces.Count == 0)
        {
            Console.WriteLine("You can't read any space in this organization yet.");
            return mapped;
        }

        Console.WriteLine();
        Console.WriteLine("Spaces:");
        for (var i = 0; i < spaces.Count; i++)
        {
            var current = mapped.TryGetValue(spaces[i].Key, out var space) ? $" -> {space.Repo}" : "";
            Console.WriteLine($"  {i + 1}. {spaces[i].Key} {spaces[i].Name}{current}");
        }

        var here = Git.FindCheckout(Environment.CurrentDirectory);
        if (here is not null && !mapped.Values.Any(x => x.Repo == here))
        {
            var space = AskSpace(spaces, $"Which space's issues should be worked on in {here}? Number, or Enter to skip:");
            if (space is not null)
            {
                mapped[space.Key] = new SpaceConfig { Repo = here, BaseRef = Git.DefaultBaseRef(here) };
            }
        }

        while (AskSpace(spaces, mapped.Count == 0 ? "Map a space to a repository? Number, or Enter to finish:" : "Map another space? Number, or Enter to finish:") is { } space)
        {
            var folder = Ask($"Folder of the {space.Key} repository (a git checkout):");
            var repo = folder.Length == 0 ? null : Git.FindCheckout(Paths.Expand(folder));
            if (repo is null)
            {
                Console.WriteLine($"{folder} isn't a git checkout.");
                continue;
            }

            mapped[space.Key] = new SpaceConfig { Repo = repo, BaseRef = Git.DefaultBaseRef(repo) };
        }

        foreach (var (key, space) in mapped)
        {
            Console.WriteLine($"  {key} -> {space.Repo} (branches from {space.BaseRef})");
        }

        return mapped;
    }

    private static SpaceSummary? AskSpace(IReadOnlyList<SpaceSummary> spaces, string question)
    {
        while (true)
        {
            var answer = Ask(question);
            if (answer.Length == 0)
            {
                return null;
            }

            if (int.TryParse(answer, out var number) && number >= 1 && number <= spaces.Count)
            {
                return spaces[number - 1];
            }

            var byKey = spaces.FirstOrDefault(x => string.Equals(x.Key, answer, StringComparison.OrdinalIgnoreCase));
            if (byKey is not null)
            {
                return byKey;
            }

            Console.WriteLine($"Enter a number from 1 to {spaces.Count}.");
        }
    }

    private static bool Confirm(string question, bool defaultYes)
    {
        var answer = Ask($"{question} [{(defaultYes ? "Y/n" : "y/N")}]");
        return answer.Length == 0 ? defaultYes : answer.StartsWith('y') || answer.StartsWith('Y');
    }

    private static string Ask(string question)
    {
        Console.Write($"{question} ");
        // End of input (e.g. nothing piped in) counts as Enter.
        return Console.ReadLine()?.Trim() ?? "";
    }
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
    private static StreamWriter? _file;

    /// <summary>Also appends every line to <paramref name="path"/> - for the Windows logon task, which has no output redirection.</summary>
    public static void ToFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public static void Info(string message)
    {
        Write(Console.Out, $"{DateTime.Now:HH:mm:ss} {message}");
    }

    public static void Error(string message)
    {
        Write(Console.Error, $"{DateTime.Now:HH:mm:ss} ERROR {message}");
    }

    private static void Write(TextWriter console, string line)
    {
        console.WriteLine(line);
        if (_file is { } file)
        {
            lock (file)
            {
                file.WriteLine(line);
            }
        }
    }
}
