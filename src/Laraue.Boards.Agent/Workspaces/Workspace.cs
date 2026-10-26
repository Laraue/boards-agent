using Laraue.Boards.Agent.Publishing;

namespace Laraue.Boards.Agent.Workspaces;

/// <summary>
/// Where the agent works on one run. In worktrees: a space with one repository is that repository's worktree; several
/// are a folder holding a worktree per repository, all on the same branch (BRD-312). In checkouts (BRD-313): the clones
/// themselves on the issue's branch, the agent starting in the first.
/// </summary>
public sealed record Workspace(string Path, string Branch, IReadOnlyList<WorkspaceRepo> Repos, SpaceContext Space, bool InCheckouts)
{
    public bool IsMultiRepo => Repos.Count > 1;

    /// <summary>Commits the branch already had when the run started - an earlier run's work this one continues.</summary>
    public int CommitsSoFar { get; init; }

    /// <summary>Open review comments on the branch's pull requests, for a run continuing earlier work (<see cref="PullRequestReviews"/>).</summary>
    public IReadOnlyList<PullRequestReview> Reviews { get; init; } = [];

    /// <summary>What the branch starts from - the one repository's, for a single-repository workspace.</summary>
    public string BaseRef => Repos[0].BaseRef;
}

/// <summary>One repository in a workspace: its worktree, or in checkout mode the clone itself.</summary>
/// <param name="BaseCommit">
/// What <see cref="BaseRef"/> resolved to in the clone when the workspace was made - what "commits of this run" are
/// counted against (in the worktree, HEAD is the run's own branch).
/// </param>
/// <param name="PreviousBranch">In checkout mode, the branch the clone was on before, to go back to when nothing was committed.</param>
public sealed record WorkspaceRepo(
    string Name,
    string Path,
    string ClonePath,
    string BaseRef,
    string? BaseCommit,
    string? Url,
    string? Description,
    string? PreviousBranch = null);
