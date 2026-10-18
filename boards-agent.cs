#!/usr/bin/env dotnet
// boards-agent: picks up Laraue Boards issues handed to the agent and works on them with Claude Code.
// Run: dotnet run boards-agent.cs -- [--config boards-agent.json] [--once]
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
Log.Info($"Watching spaces: {string.Join(", ", config.Spaces.Keys)}. Dry run - nothing is claimed yet.");

try
{
    while (!stop.IsCancellationRequested)
    {
        foreach (var spaceKey in config.Spaces.Keys)
        {
            var runs = await boards.ListReadyRuns(spaceKey, me.User.Id, stop.Token);
            if (runs.Count == 0)
            {
                Log.Info($"{spaceKey}: no Ready runs.");
                continue;
            }

            foreach (var run in runs)
            {
                Log.Info($"{spaceKey}: Ready run {run.Id} for {run.IssueKey} \"{run.IssueTitle}\" (handed off by {run.CreatedBy} at {run.CreatedAt:u}).");
            }
        }

        if (options.Once)
        {
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
}

Log.Info("Stopped.");
return 0;

/// <summary>Command-line options.</summary>
sealed record RunnerArgs(string ConfigPath, bool Once)
{
    public static RunnerArgs Parse(string[] args)
    {
        var configPath = "boards-agent.json";
        var once = false;
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
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}. Usage: boards-agent [--config <file>] [--once]");
            }
        }

        return new RunnerArgs(configPath, once);
    }
}

/// <summary>The runner's settings, read from a JSON file. The API key itself is never stored there.</summary>
sealed record RunnerConfig
{
    /// <summary>The Boards MCP endpoint.</summary>
    public string McpUrl { get; init; } = "http://localhost:5202/mcp";

    /// <summary>The environment variable holding the API key of the user the runner works for.</summary>
    public string ApiKeyEnv { get; init; } = "BOARDS_AGENT_API_KEY";

    /// <summary>Space key -> local checkout of that space's repository. Only these spaces are polled.</summary>
    public Dictionary<string, string> Spaces { get; init; } = [];

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
            Spaces = config.Spaces.ToDictionary(x => x.Key, x => Paths.Expand(x.Value)),
            WorktreeFolder = Paths.Expand(config.WorktreeFolder),
        };
    }
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

    private async Task<T> Call<T>(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: ct);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(x => x.Text));
        if (result.IsError == true)
        {
            throw new BoardsToolException(tool, text);
        }

        return JsonSerializer.Deserialize<T>(text, Json.Options)
            ?? throw new BoardsToolException(tool, "empty result");
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
