#!/usr/bin/env dotnet
// boards-agent: picks up Laraue Boards issues handed to the agent and works on them with Claude Code.
// Run: dotnet run boards-agent.cs -- [--config boards-agent.json] [--once] [--dry-run]
#:package ModelContextProtocol.Core@1.4.0
#:property PublishAot=false

using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var options = RunnerArgs.Parse(args);
var config = RunnerConfig.Load(options.ConfigPath);

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
Log.Info($"Watching spaces: {string.Join(", ", config.Spaces.Keys)}.{(options.DryRun ? " Dry run - nothing is claimed." : "")}");

var runner = new Runner(boards, config, me.User.Id);
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
sealed class Runner(BoardsClient boards, RunnerConfig config, Guid userId)
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
        try
        {
            var statuses = await FindEpicStatuses(spaceKey, run.IssueKey, ct);
            var inProgress = statuses.Statuses.First(x => x.CategoryId == StatusCategories.InProgress);

            var worktree = Git.CreateWorktree(space.Repo, space.BaseRef, config.WorktreeFolder, run.IssueKey);
            Log.Info($"{run.IssueKey}: worktree {worktree.Path} on branch {worktree.Branch}.");

            await boards.EditIssueStatus(
                run.IssueKey,
                inProgress.Id,
                $"boards-agent picked this up on {Environment.MachineName}, branch `{worktree.Branch}`.",
                ct);
            Log.Info($"{run.IssueKey}: moved to {inProgress.Name} ({statuses.EpicName}).");

            // Step 2: the Claude Code part isn't wired yet.
            await Fail(run, "boards-agent: the work step isn't implemented yet (runner step 2 test).", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"{run.IssueKey}: {e.Message}");
            await Fail(run, $"boards-agent couldn't start: {e.Message}", ct);
        }

        return true;
    }

    /// <summary>
    /// The statuses of the issue's epic. MCP doesn't return an issue's epic, so this looks for the issue among the
    /// user's not-started issues of each epic that can take a hand-off (Created, InProgress and Review statuses).
    /// </summary>
    private async Task<EpicStatusSummary> FindEpicStatuses(string spaceKey, string issueKey, CancellationToken ct)
    {
        var epics = await boards.ListStatuses(spaceKey, ct);
        foreach (var epic in epics.Where(e => StatusCategories.CanTakeHandOff(e.Statuses)))
        {
            if (await boards.HasIssue(spaceKey, epic.EpicId, userId, StatusCategories.Created, issueKey, ct))
            {
                return epic;
            }
        }

        throw new InvalidOperationException($"couldn't find the epic of {issueKey} in {spaceKey}");
    }

    /// <summary>Records why the run failed, as a comment people see and on the run itself.</summary>
    private async Task Fail(AgentRunSummary run, string reason, CancellationToken ct)
    {
        if (reason.Length > MaxReasonLength)
        {
            reason = reason[..MaxReasonLength];
        }

        try
        {
            await boards.CreateComment(run.IssueKey, reason, ct);
            if (await boards.FinishAgentRun(run.Id, "Failed", reason, ct))
            {
                Log.Info($"{run.IssueKey}: run {run.Id} finished as Failed.");
            }
            else
            {
                Log.Info($"{run.IssueKey}: run {run.Id} was cancelled meanwhile.");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"{run.IssueKey}: couldn't report the failure: {e.Message}");
        }
    }
}

/// <summary>Status category ids, as list_statuses returns them.</summary>
static class StatusCategories
{
    public const int Created = 0;
    public const int InProgress = 1;
    public const int Completed = 2;
    public const int Review = 3;

    /// <summary>What the server requires of an epic to hand one of its issues off.</summary>
    public static bool CanTakeHandOff(IReadOnlyList<StatusSummary> statuses)
    {
        return statuses.Any(x => x.CategoryId == Created)
            && statuses.Any(x => x.CategoryId == InProgress)
            && statuses.Any(x => x.CategoryId == Review);
    }
}

sealed record Worktree(string Path, string Branch);

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
            return new Worktree(path, branch);
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

        return new Worktree(path, branch);
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
sealed record RunnerArgs(string ConfigPath, bool Once, bool DryRun)
{
    public static RunnerArgs Parse(string[] args)
    {
        var configPath = "boards-agent.json";
        var once = false;
        var dryRun = false;
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
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}. Usage: boards-agent [--config <file>] [--once] [--dry-run]");
            }
        }

        return new RunnerArgs(configPath, once, dryRun);
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

    /// <summary>The Claude Code executable.</summary>
    public string ClaudePath { get; init; } = "claude";

    /// <summary>Claude Code's --max-turns.</summary>
    public int MaxTurns { get; init; } = 50;

    /// <summary>Claude Code's --max-budget-usd, if the installed version supports it.</summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>A run taking longer than this is stopped and failed.</summary>
    public int TimeoutMinutes { get; init; } = 60;

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

    /// <summary>Whether an issue with this key is among the epic's issues of that assignee and status category.</summary>
    public async Task<bool> HasIssue(string spaceKey, long epicId, Guid assigneeId, int statusCategoryId, string issueKey, CancellationToken ct)
    {
        for (var page = 0; ; page++)
        {
            var issues = await Call<IssueListPage>(
                "list_issues",
                new Dictionary<string, object?>
                {
                    ["spaceKey"] = spaceKey,
                    ["epicId"] = epicId,
                    ["assigneeId"] = assigneeId,
                    ["statusCategoryId"] = statusCategoryId,
                    ["page"] = page,
                    ["count"] = PageSize,
                },
                ct);
            if (issues.Issues.Any(x => x.Key == issueKey))
            {
                return true;
            }

            if (!issues.HasNextPage)
            {
                return false;
            }
        }
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

sealed record IssueSummary(string Key, string Title, string Status, int StatusCategoryId);

sealed record IssueListPage(IReadOnlyList<IssueSummary> Issues, long Page, bool HasNextPage);

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
