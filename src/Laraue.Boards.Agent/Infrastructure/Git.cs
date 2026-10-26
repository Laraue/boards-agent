using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Infrastructure;

public static class Git
{
    /// <summary>
    /// A worktree of <paramref name="repo"/> at <paramref name="path"/> on <paramref name="branch"/>. Reuses the worktree,
    /// or the branch, left by an earlier run of the same issue. A new branch starts from <paramref name="baseRef"/>
    /// fetched just before, so e.g. origin/main isn't however old the checkout's last fetch was.
    /// </summary>
    public static void CreateWorktree(string repo, string baseRef, string path, string branch)
    {
        if (Directory.Exists(path))
        {
            if (CurrentBranch(path) == branch)
            {
                return;
            }

            // The issue's earlier worktree, on a branch that was merged meanwhile: this run starts a new branch.
            if (Run(repo, ["worktree", "remove", path], throwOnError: false) is { ExitCode: not 0 } removal)
            {
                throw new InvalidOperationException(
                    $"{path} is a worktree on another branch that can't be removed ({removal.Error.Trim()}); remove it (git worktree remove) to work on {branch}");
            }
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
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
    }

    /// <summary>
    /// Checkout mode: switches the clone to <paramref name="branch"/> - the one an earlier run of the issue left, or a
    /// new one from <paramref name="baseRef"/> fetched just before.
    /// </summary>
    public static void SwitchToBranch(string repo, string baseRef, string branch)
    {
        var branchExists = Run(repo, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], throwOnError: false).ExitCode == 0;
        if (branchExists)
        {
            Run(repo, ["switch", branch]);
        }
        else
        {
            FetchRemoteRef(repo, baseRef);
            Run(repo, ["switch", "--no-track", "-c", branch, baseRef]);
        }
    }

    /// <summary>
    /// Checkout mode: back to <paramref name="previous"/> and deletes <paramref name="branch"/> if it's still empty
    /// (<see cref="DeleteBranchIfEmpty"/>). Best effort: what's left is only clutter.
    /// </summary>
    public static void SwitchBack(string repo, string previous, string branch, string baseCommit)
    {
        if (Run(repo, ["switch", previous], throwOnError: false).ExitCode == 0)
        {
            DeleteBranchIfEmpty(repo, branch, baseCommit);
        }
    }

    /// <summary>
    /// The local branches named <paramref name="prefix"/> or <paramref name="prefix"/>-something, newest commit first, e.g.
    /// an earlier run's agent/BRD-314 or agent/BRD-314-sort-epics.
    /// </summary>
    public static IReadOnlyList<string> FindBranches(string repo, string prefix)
    {
        var result = Run(
            repo,
            ["for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", $"refs/heads/{prefix}", $"refs/heads/{prefix}-*"],
            throwOnError: false);
        return result.ExitCode == 0
            ? result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x == prefix || x.StartsWith($"{prefix}-", StringComparison.Ordinal))
                .ToList()
            : [];
    }

    /// <summary>Whether <paramref name="branch"/> exists here, or on origin as far as the last fetch knows.</summary>
    public static bool BranchExists(string repo, string branch)
    {
        return Run(repo, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], throwOnError: false).ExitCode == 0
            || Run(repo, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}"], throwOnError: false).ExitCode == 0;
    }

    /// <summary>
    /// Whether the local <paramref name="branch"/> has commits of its own and all of them are in <paramref name="baseRef"/>,
    /// fetched first: it was merged (or fast-forwarded). Its own commits are the ones since it was created, by its reflog;
    /// a branch nobody committed to isn't merged, just empty. A squash merge leaves the branch's commits out of the base,
    /// so it isn't seen here.
    /// </summary>
    public static bool IsMergedInto(string repo, string branch, string baseRef)
    {
        FetchRemoteRef(repo, baseRef);
        if (Run(repo, ["merge-base", "--is-ancestor", $"refs/heads/{branch}", baseRef], throwOnError: false).ExitCode != 0)
        {
            return false;
        }

        var created = Run(repo, ["reflog", "show", "--format=%H", $"refs/heads/{branch}"], throwOnError: false).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return created != ResolveCommit(repo, $"refs/heads/{branch}");
    }

    /// <summary>
    /// Deletes <paramref name="branch"/> when it has no commits beyond <paramref name="baseCommit"/>, what the run
    /// started from. Not <c>branch -d</c>: that compares with the local default branch, which may be behind the remote
    /// one the branch started from, and then refuses to delete an empty branch.
    /// </summary>
    private static void DeleteBranchIfEmpty(string repo, string branch, string baseCommit)
    {
        var result = Run(repo, ["rev-list", "--count", $"{baseCommit}..refs/heads/{branch}"], throwOnError: false);
        if (result.ExitCode == 0 && result.Output.Trim() == "0")
        {
            Run(repo, ["branch", "-D", branch], throwOnError: false);
        }
    }

    /// <summary>Pushes <paramref name="branch"/> to origin, setting it as the upstream. Never prompts for credentials.</summary>
    public static ProcessResult Push(string repo, string branch)
    {
        return Run(repo, ["push", "--set-upstream", "origin", branch], throwOnError: false);
    }

    /// <summary>The branch the checkout is on, or null when HEAD is detached.</summary>
    public static string? CurrentBranch(string repo)
    {
        var result = Run(repo, ["symbolic-ref", "--quiet", "--short", "HEAD"], throwOnError: false);
        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } branch ? branch : null;
    }

    /// <summary>How many files have uncommitted changes, untracked ones included; -1 when git can't tell.</summary>
    public static int UncommittedFiles(string repo)
    {
        var result = Run(repo, ["status", "--porcelain"], throwOnError: false);
        return result.ExitCode == 0
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : -1;
    }

    /// <summary>The commit a ref points to in the repository, or null when it doesn't resolve.</summary>
    public static string? ResolveCommit(string repo, string reference)
    {
        var result = Run(repo, ["rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"], throwOnError: false);
        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } commit ? commit : null;
    }

    /// <summary>
    /// How many commits <paramref name="branch"/> has beyond <paramref name="baseCommit"/>; -1 when that can't be told,
    /// e.g. the branch was deleted. Not HEAD: in checkout mode, <see cref="WorkspaceManager.Finish"/> switches a clone the agent
    /// didn't commit to back to its previous branch, whose commits aren't the agent's.
    /// </summary>
    public static int CommitsSince(string repo, string baseCommit, string branch)
    {
        var result = Run(repo, ["rev-list", "--count", $"{baseCommit}..refs/heads/{branch}"], throwOnError: false);
        return result.ExitCode == 0 && int.TryParse(result.Output.Trim(), out var count) ? count : -1;
    }

    /// <summary>
    /// Removes a worktree and its branch if it's still empty (<see cref="DeleteBranchIfEmpty"/>). A worktree with
    /// uncommitted changes isn't removed, and so neither is its branch. Best effort: what's left is only clutter.
    /// </summary>
    public static void RemoveWorktree(string repo, string worktree, string branch, string baseCommit)
    {
        if (Run(repo, ["worktree", "remove", worktree], throwOnError: false).ExitCode == 0)
        {
            DeleteBranchIfEmpty(repo, branch, baseCommit);
        }
    }

    /// <summary>The URL of the checkout's origin remote, or null when it has none.</summary>
    public static string? OriginUrl(string repo)
    {
        var result = Run(repo, ["remote", "get-url", "origin"], throwOnError: false);
        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } url ? url : null;
    }

    /// <summary>The checkout's remotes, normalized (<see cref="RepoUrl"/>).</summary>
    public static IReadOnlyList<string> RemoteUrls(string repo)
    {
        return Run(repo, ["remote", "-v"], throwOnError: false).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', ' ').ElementAtOrDefault(1))
            .Select(url => url is null ? null : RepoUrl.Normalize(url))
            .OfType<string>()
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The main checkout <paramref name="folder"/> belongs to - for a worktree (e.g. a <c>.claude/worktrees/...</c>
    /// copy), the repository it was made from. Null when the folder isn't in a git checkout.
    /// </summary>
    public static string? MainCheckout(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var result = Run(folder, ["rev-parse", "--path-format=absolute", "--git-common-dir"], throwOnError: false);
        var commonDir = result.Output.Trim();
        return result.ExitCode == 0 && commonDir.Length > 0
            ? System.IO.Path.GetDirectoryName(commonDir.TrimEnd('/', '\\'))
            : null;
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
