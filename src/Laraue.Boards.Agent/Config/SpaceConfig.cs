namespace Laraue.Boards.Agent.Config;

/// <summary>
/// A repository mapped to a space by hand - for a repository the space has no link to. Repositories the space links to
/// are found by themselves (<see cref="SpaceResolver"/>).
/// </summary>
sealed record SpaceConfig
{
    /// <summary>The local checkout. Worktrees are created from it, the checkout itself isn't touched.</summary>
    public string? Repo { get; set; }

    /// <summary>What an issue's branch starts from, e.g. origin/main.</summary>
    public string BaseRef { get; set; } = "HEAD";
}
