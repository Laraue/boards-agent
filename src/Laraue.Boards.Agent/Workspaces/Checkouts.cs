using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Workspaces;

/// <summary>Checkout mode (BRD-313): what has to be asked about before the agent may switch your clones.</summary>
static class Checkouts
{
    /// <summary>
    /// One line per clone that isn't ready to be switched: uncommitted changes, or a branch other than the default one
    /// (e.g. the previous issue's, still under review). Empty when every clone is clean and on its default branch.
    /// </summary>
    /// <param name="issueBranch">
    /// The issue's own branch: a clone already on it is no problem whatever it has - uncommitted changes there are the
    /// agent's unfinished work on this issue (a run cut off by its turn limit), which the next run continues.
    /// </param>
    public static IReadOnlyList<CheckoutProblem> Problems(SpaceContext space, string issueBranch)
    {
        var problems = new List<CheckoutProblem>();
        foreach (var repo in space.Repos)
        {
            var branch = Git.CurrentBranch(repo.ClonePath);
            if (branch == issueBranch)
            {
                continue;
            }

            var uncommitted = Git.UncommittedFiles(repo.ClonePath);
            var defaultBranch = DefaultBranch(repo.BaseRef);
            var issues = new List<string>();
            if (uncommitted != 0)
            {
                issues.Add(uncommitted < 0 ? "git status failed" : $"{uncommitted} uncommitted file{(uncommitted == 1 ? "" : "s")}");
            }

            if (defaultBranch is not null && branch != defaultBranch)
            {
                issues.Add(branch is null ? "detached HEAD" : $"on {branch}");
            }

            if (issues.Count > 0)
            {
                problems.Add(new CheckoutProblem($"{repo.Name}: {string.Join(", ", issues)}", HasChanges: uncommitted != 0));
            }
        }

        return problems;
    }

    /// <summary>The local default branch a base ref stands for: origin/main -> main; null for HEAD.</summary>
    private static string? DefaultBranch(string baseRef)
    {
        var slash = baseRef.IndexOf('/');
        return slash > 0 ? baseRef[(slash + 1)..] : null;
    }
}

/// <summary>A clone that isn't ready to be switched. <paramref name="HasChanges"/>: it has uncommitted changes (or git couldn't tell).</summary>
sealed record CheckoutProblem(string Text, bool HasChanges);
