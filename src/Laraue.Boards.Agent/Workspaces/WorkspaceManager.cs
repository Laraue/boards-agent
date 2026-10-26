using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Workspaces;

public static class WorkspaceManager
{
    /// <summary>How long the title part of a new branch name can get, e.g. agent/BRD-314-sort-epics-in-space-detail-page.</summary>
    private const int MaxSlugLength = 40;

    /// <summary>
    /// The issue's branch: the one an earlier run left in any of the space's repositories - found by the issue key, so
    /// renaming the issue doesn't start a new branch - else agent/KEY-slug-of-the-title.
    /// </summary>
    public static string BranchName(SpaceContext space, string issueKey, string issueTitle)
    {
        var prefix = $"agent/{issueKey}";
        foreach (var repo in space.Repos)
        {
            if (Git.FindBranch(repo.ClonePath, prefix) is { } existing)
            {
                return existing;
            }
        }

        var slug = Slug(issueTitle);
        return slug.Length == 0 ? prefix : $"{prefix}-{slug}";
    }

    /// <summary>
    /// Lower-case ASCII words of <paramref name="title"/> joined by hyphens, accents dropped, cut at a word boundary to
    /// <see cref="MaxSlugLength"/>. Empty when the title has no ASCII letters or digits (e.g. it's in Cyrillic).
    /// </summary>
    public static string Slug(string title)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in title.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark
                && builder.Length > 0
                && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length <= MaxSlugLength)
        {
            return slug;
        }

        var cut = slug.LastIndexOf('-', MaxSlugLength);
        return cut > 0 ? slug[..cut] : slug[..MaxSlugLength];
    }

    public static Workspace Create(SpaceContext space, string worktreeFolder, string issueKey, string issueTitle)
    {
        var branch = BranchName(space, issueKey, issueTitle);
        var root = Path.Combine(worktreeFolder, issueKey);
        if (space.Repos.Count == 1)
        {
            var repo = space.Repos[0];
            Git.CreateWorktree(repo.ClonePath, repo.BaseRef, root, branch);
            return WithCommitsSoFar(new Workspace(root, branch, [ToWorkspaceRepo(repo, root)], space, InCheckouts: false));
        }

        if (File.Exists(Path.Combine(root, ".git")))
        {
            throw new InvalidOperationException(
                $"{root} is a worktree left by an earlier run with one repository; remove it (git worktree remove) to work on {issueKey} across repositories");
        }

        Directory.CreateDirectory(root);
        var repos = new List<WorkspaceRepo>();
        foreach (var repo in space.Repos)
        {
            var path = Path.Combine(root, repo.Name);
            Git.CreateWorktree(repo.ClonePath, repo.BaseRef, path, branch);
            repos.Add(ToWorkspaceRepo(repo, path));
        }

        return WithCommitsSoFar(new Workspace(root, branch, repos, space, InCheckouts: false));
    }

    /// <summary>
    /// Checkout mode: switches each of the space's clones to the issue's branch - a new one from the freshly fetched
    /// default branch, or the one an earlier run of the issue left. <see cref="Checkouts.Problems"/> was asked about
    /// before; a clone that got uncommitted changes since is refused, never carried over to the issue's branch.
    /// </summary>
    public static Workspace CreateInCheckouts(SpaceContext space, string issueKey, string issueTitle)
    {
        var branch = BranchName(space, issueKey, issueTitle);
        foreach (var repo in space.Repos)
        {
            // Changes on the issue's own branch are the agent's unfinished work, which this run continues.
            if (Git.CurrentBranch(repo.ClonePath) != branch && Git.UncommittedFiles(repo.ClonePath) > 0)
            {
                throw new InvalidOperationException($"{repo.Name} ({repo.ClonePath}) has uncommitted changes");
            }
        }

        var repos = new List<WorkspaceRepo>();
        foreach (var repo in space.Repos)
        {
            // Already on the issue's branch (an earlier run of it): nothing to go back to afterwards.
            var current = Git.CurrentBranch(repo.ClonePath);
            var previous = current == branch ? null : current;
            Git.SwitchToBranch(repo.ClonePath, repo.BaseRef, branch);
            repos.Add(ToWorkspaceRepo(repo, repo.ClonePath) with { PreviousBranch = previous });
        }

        return WithCommitsSoFar(new Workspace(repos[0].Path, branch, repos, space, InCheckouts: true));
    }

    /// <summary>The workspace with <see cref="Workspace.CommitsSoFar"/>: what an earlier run of the issue left on its branch.</summary>
    private static Workspace WithCommitsSoFar(Workspace workspace)
    {
        return workspace with
        {
            CommitsSoFar = workspace.Repos
                .Where(repo => repo.BaseCommit is not null)
                .Sum(repo => Math.Max(0, Git.CommitsSince(repo.Path, repo.BaseCommit!, workspace.Branch))),
        };
    }

    /// <summary>
    /// After a run: puts back what the agent didn't commit to, and returns one line per repository it did, e.g.
    /// "web-app: `agent/BRD-42`, 3 commits". In worktrees, across repositories, unchanged worktrees are removed. In
    /// checkouts, an unchanged clone goes back to its previous branch and the empty branch is deleted.
    /// </summary>
    public static IReadOnlyList<string> Finish(Workspace workspace)
    {
        if (!workspace.InCheckouts)
        {
            return workspace.IsMultiRepo ? RemoveUnchanged(workspace) : [];
        }

        var changed = new List<string>();
        foreach (var repo in workspace.Repos)
        {
            var commits = repo.BaseCommit is null ? -1 : Git.CommitsSince(repo.Path, repo.BaseCommit, workspace.Branch);
            var uncommitted = Git.UncommittedFiles(repo.Path);
            var left = uncommitted == 0 ? "" : $", {uncommitted} uncommitted file{(uncommitted == 1 ? "" : "s")} left";
            if (commits == 0 && uncommitted == 0 && repo.PreviousBranch is { } previous)
            {
                Git.SwitchBack(repo.Path, previous, workspace.Branch, repo.BaseCommit!);
            }
            else if (commits < 0)
            {
                changed.Add($"{repo.Name}: `{workspace.Branch}` (couldn't count its commits){left}");
            }
            else if (commits > 0 || uncommitted > 0)
            {
                changed.Add($"{repo.Name}: `{workspace.Branch}`, {commits} commit{(commits == 1 ? "" : "s")}{left}");
            }
        }

        return changed;
    }

    /// <summary>
    /// After a run across repositories: removes the worktree and branch of every repository the agent didn't commit to,
    /// and returns one line per repository it did, e.g. "web-app: `agent/BRD-42`, 3 commits".
    /// </summary>
    private static IReadOnlyList<string> RemoveUnchanged(Workspace workspace)
    {
        var changed = new List<string>();
        foreach (var repo in workspace.Repos)
        {
            var commits = repo.BaseCommit is null ? -1 : Git.CommitsSince(repo.Path, repo.BaseCommit, workspace.Branch);
            if (commits > 0)
            {
                changed.Add($"{repo.Name}: `{workspace.Branch}`, {commits} commit{(commits == 1 ? "" : "s")}");
            }
            else if (commits < 0)
            {
                // Can't tell: keep it rather than risk removing work.
                changed.Add($"{repo.Name}: `{workspace.Branch}` (kept: couldn't count its commits)");
            }
            else
            {
                Git.RemoveWorktree(repo.ClonePath, repo.Path, workspace.Branch, repo.BaseCommit!);
            }
        }

        return changed;
    }

    private static WorkspaceRepo ToWorkspaceRepo(SpaceRepo repo, string path)
    {
        return new WorkspaceRepo(
            repo.Name,
            path,
            repo.ClonePath,
            repo.BaseRef,
            Git.ResolveCommit(repo.ClonePath, repo.BaseRef),
            repo.Url,
            repo.Description);
    }
}
