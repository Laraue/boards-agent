using System.Text.Json;
using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;

namespace Laraue.Boards.Agent.Config;

/// <summary>
/// The runner's settings, written by `lba setup` to <see cref="DefaultPath"/>. The API key itself is never
/// stored there - it's in the OS credential store (<see cref="CredentialStore"/>).
/// </summary>
public sealed record RunnerConfig
{
    // Settable, not init-only: the JSON source generator sets every missing init-only property to null/0, overriding
    // the defaults below, so a config written before a setting existed would get no value for it.
    /// <summary>The Boards web app, where setup sends the person to allow the sign-in.</summary>
    public string WebUrl { get; set; } = "https://boards.laraue.com";

    /// <summary>The Boards web API, which issues the API key at setup.</summary>
    public string ApiUrl { get; set; } = "https://boards.laraue.com/boards-api";

    /// <summary>The Boards MCP endpoint.</summary>
    public string McpUrl { get; set; } = "https://boards.laraue.com/boards-mcp/mcp";

    /// <summary>The organization the API key belongs to - its credential store entry is named after it.</summary>
    public long? OrganizationId { get; set; }

    public string? OrganizationName { get; set; }

    /// <summary>
    /// An environment variable that, when set, overrides the stored key (e.g. on a machine without a credential
    /// store). Also how Claude Code receives the key: the runner sets it for the Claude it starts.
    /// </summary>
    public string ApiKeyEnv { get; set; } = "BOARDS_AGENT_API_KEY";

    /// <summary>
    /// Folders the space's repositories are cloned under (searched three levels deep). A space's links to repositories
    /// (Boards, BRD-312) are matched against these clones' git remotes, so nothing is mapped by hand.
    /// </summary>
    public string[] CodeFolders { get; set; } = [];

    /// <summary>Space key -> a repository mapped by hand, for one the space has no link to.</summary>
    public Dictionary<string, SpaceConfig> Spaces { get; set; } = [];

    /// <summary>Where the runner creates a git worktree per issue, and keeps the agents' logs.</summary>
    public string WorktreeFolder { get; set; } = "~/boards-agent-worktrees";

    /// <summary>
    /// Where the agent works (BRD-313): <see cref="WorkspaceModes.Worktree"/> - a git worktree per issue, your clones
    /// aren't touched - or <see cref="WorkspaceModes.Checkout"/> - your own clones, switched to the issue's branch, so
    /// you review and run the result where you always work. Checkout mode asks before touching a clone that has
    /// uncommitted changes or isn't on its default branch.
    /// </summary>
    public string Workspace { get; set; } = WorkspaceModes.Worktree;

    public bool InCheckouts => Workspace.Equals(WorkspaceModes.Checkout, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Claude Code executable. "claude" means the one on PATH, or else the newest one bundled with the Claude
    /// desktop app.
    /// </summary>
    public string ClaudePath { get; set; } = "claude";

    /// <summary>
    /// Which coding agent works on the issues: <see cref="CodingAgents.Claude"/> (Claude Code) or
    /// <see cref="CodingAgents.Codex"/>. Setup asks when both are installed.
    /// </summary>
    public string Agent { get; set; } = CodingAgents.Claude;

    /// <summary>The Codex executable. "codex" means the one on PATH.</summary>
    public string CodexPath { get; set; } = "codex";

    /// <summary>
    /// Whether Codex's commands may reach the network. Off: nothing a command runs can push code or send data anywhere
    /// (the Boards MCP server still works - Codex itself talks to it), but builds can't download packages that aren't
    /// cached yet. On: restores work, and so would a <c>git push</c>.
    /// </summary>
    public bool CodexNetworkAccess { get; set; }

    /// <summary>The model Codex uses; null for Codex's default.</summary>
    public string? CodexModel { get; set; }

    /// <summary>
    /// What Claude Code may do without asking, besides editing files in the worktree (--allowedTools). Anything else
    /// is denied, since nobody is there to approve it. Codex has no such list: its sandbox limits it instead.
    /// </summary>
    public string[] AllowedTools { get; set; } =
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

    /// <summary>Claude Code's --max-turns. Codex has no turn limit; the timeout is its only limit.</summary>
    public int MaxTurns { get; set; } = DefaultMaxTurns;

    /// <summary>
    /// 150: a task across two repositories (backend and web, with tests) ran out at the old default of 50 (MIN-22). The
    /// timeout still bounds a run that goes wrong.
    /// </summary>
    public const int DefaultMaxTurns = 150;

    /// <summary>The default before 0.4.0-beta.5, written into every config setup saved - read as "the default".</summary>
    private const int OldDefaultMaxTurns = 50;

    /// <summary>Claude Code's --max-budget-usd, if the installed version supports it. Not available for Codex.</summary>
    public decimal? MaxBudgetUsd { get; set; }

    /// <summary>
    /// How many more rounds a run gets when the agent stops at <see cref="MaxTurns"/> without finishing: the runner
    /// resumes its session to continue. The timeout still bounds the whole run. Claude Code only.
    /// </summary>
    public int MaxContinuations { get; set; } = 2;

    /// <summary>A run taking longer than this is stopped and failed.</summary>
    public int TimeoutMinutes { get; set; } = 60;

    /// <summary>How often a working run is checked for a cancel and the timeout.</summary>
    public int CancelCheckSeconds { get; set; } = 30;

    /// <summary>
    /// System notifications when a run starts, is ready for review, fails or is cancelled, shown as
    /// <see cref="Notifications.AppName"/> (BRD-315).
    /// </summary>
    public bool Notifications { get; set; } = true;

    /// <summary>
    /// What happens to the agent's work after a Done run (BRD-315): <see cref="PublishModes.Off"/> - nothing, you push
    /// the branch yourself; <see cref="PublishModes.Push"/> - the runner pushes it; <see cref="PublishModes.PullRequest"/>
    /// - it also opens a pull request with the GitHub CLI. The agent itself never pushes.
    /// </summary>
    public string Publish { get; set; } = PublishModes.Off;

    /// <summary>
    /// Whether Claude Code may use the person's Chrome (the Claude in Chrome extension) in interactive `lba continue`
    /// sessions, e.g. to open the app and check a change while they watch. Background runs never do: nobody is there to
    /// approve browser actions. Asked in setup when the agent is Claude Code; off by default.
    /// </summary>
    public bool Chrome { get; set; }

    /// <summary>
    /// Whether the background service looks for a new release once a day and says so (a notification and the log). It
    /// never updates by itself: `lba update` does.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>How often Ready runs are polled.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Whose open pull request review comments a re-run of an issue gets in its prompt (<see cref="PullRequestReviews"/>),
    /// a <see cref="ReviewCommentLevels"/> value. Asked in setup; per repository in <see cref="RepositoryReviewComments"/>.
    /// </summary>
    public string ReviewComments { get; set; } = ReviewCommentLevels.Collaborators;

    /// <summary>
    /// Repository (normalized URL, e.g. github.com/acme/api) -> its own <see cref="ReviewComments"/> level, set with
    /// `lba review-comments`.
    /// </summary>
    public Dictionary<string, string> RepositoryReviewComments { get; set; } = [];

    /// <summary>
    /// Whether the runner resolves a review thread after replying that every comment in it was fixed (<see cref="ReviewReplies"/>).
    /// Off by default: the reviewer resolves it once they've checked the fix.
    /// </summary>
    public bool ResolveReviewThreads { get; set; }

    /// <summary>The review comments level of the repository with these (normalized) remotes.</summary>
    public string ReviewCommentsFor(IEnumerable<string> remotes)
    {
        foreach (var remote in remotes)
        {
            if (RepositoryReviewComments.TryGetValue(remote, out var level))
            {
                return level.ToLowerInvariant();
            }
        }

        return ReviewComments.ToLowerInvariant();
    }

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
            ?? throw new FileNotFoundException($"No config at {path}. Run `lba setup` first.");
        if (config.CodeFolders.Length == 0 && config.Spaces.Count == 0)
        {
            throw new InvalidOperationException($"{path}: no code folder to find repositories in. Run `lba setup`.");
        }

        if (!PublishModes.All.Contains(config.Publish, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{path}: unknown publish \"{config.Publish}\": use \"{PublishModes.Off}\", \"{PublishModes.Push}\" or \"{PublishModes.PullRequest}\".");
        }

        foreach (var level in config.RepositoryReviewComments.Values.Prepend(config.ReviewComments))
        {
            if (!ReviewCommentLevels.All.Contains(level, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{path}: unknown review comments level \"{level}\": use {string.Join(", ", ReviewCommentLevels.All.Select(x => $"\"{x}\""))}.");
            }
        }

        if (!WorkspaceModes.All.Contains(config.Workspace, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{path}: unknown workspace \"{config.Workspace}\": use \"{WorkspaceModes.Worktree}\" or \"{WorkspaceModes.Checkout}\".");
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
            MaxTurns = config.MaxTurns == OldDefaultMaxTurns ? DefaultMaxTurns : config.MaxTurns,
            Spaces = config.Spaces.ToDictionary(
                x => x.Key,
                x => x.Value with { Repo = x.Value.Repo is { Length: > 0 } repo ? Paths.Expand(repo) : null }),
            WorktreeFolder = Paths.Expand(config.WorktreeFolder),
        };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, RunnerJson.Default.RunnerConfig));
    }
}
