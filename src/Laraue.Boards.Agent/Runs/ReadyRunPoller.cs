using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Runs;

/// <summary>
/// One look at the person's Ready runs, in every space: the first one this machine can work on is claimed and worked on.
/// A run of a space with no repository cloned here is left for another machine; in checkout mode, one the person said
/// "Not now" to is asked about again later.
/// </summary>
public sealed class ReadyRunPoller(
    IBoardsClient boards,
    RunnerConfig config,
    SpaceResolver spaces,
    Runner runner,
    Guid userId,
    bool dryRun)
{
    /// <summary>How long a "Not now" holds before the person is asked again.</summary>
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(10);

    /// <summary>Runs skipped for having no repository here, so each is logged once rather than every poll.</summary>
    private readonly HashSet<long> _skippedRuns = [];

    /// <summary>Checkout mode: runs the person said "Not now" to, and when to ask about them again.</summary>
    private readonly Dictionary<long, DateTime> _postponedRuns = [];

    /// <summary>True when a run was worked on - one at a time, so the caller polls again from the start.</summary>
    public async Task<bool> PollOnce(CancellationToken ct)
    {
        if (!dryRun)
        {
            await runner.RecoverInterruptedRun(ct);
        }

        var runs = await boards.ListReadyRuns(spaceKey: null, userId, ct);
        var resolved = new Dictionary<string, SpaceContext>();
        foreach (var run in runs)
        {
            var spaceKey = IssueKeys.SpaceKey(run.IssueKey);
            if (!resolved.TryGetValue(spaceKey, out var space))
            {
                space = await spaces.Resolve(spaceKey, ct);
                resolved[spaceKey] = space;
            }

            if (!CanWorkOn(space, run))
            {
                continue;
            }

            if (!dryRun && await runner.TryProcess(space, run, ct))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this machine works on the run now: a repository of its space is cloned here, and the clones are ready.</summary>
    private bool CanWorkOn(SpaceContext space, AgentRunSummary run)
    {
        if (space.Repos.Count == 0)
        {
            if (_skippedRuns.Add(run.Id))
            {
                var links = space.OtherLinks.Count == 0 ? "the space has no links" : $"links: {string.Join(", ", space.OtherLinks.Select(x => x.Url))}";
                Log.Info($"{run.IssueKey}: Ready, but none of {space.Key}'s repositories is cloned under {string.Join(", ", config.CodeFolders)} ({links}) - left for another machine.");
            }

            return false;
        }

        if (_postponedRuns.TryGetValue(run.Id, out var askAgainAt) && DateTime.UtcNow < askAgainAt)
        {
            return false;
        }

        Log.Info($"{space.Key}: Ready run {run.Id} for {run.IssueKey} \"{run.IssueTitle}\" (handed off by {run.CreatedBy} at {run.CreatedAt:u}), repositories: {string.Join(", ", space.Repos.Select(x => x.Name))}.");
        if (!dryRun && config.InCheckouts && !CheckoutsReady(space, run))
        {
            _postponedRuns[run.Id] = DateTime.UtcNow.Add(AskAgainAfter);
            Log.Info($"{run.IssueKey}: not now - asking again in {AskAgainAfter.TotalMinutes:0} minutes.");
            return false;
        }

        _postponedRuns.Remove(run.Id);
        return true;
    }

    /// <summary>
    /// Checkout mode: true once no clone of the space has uncommitted changes - the one case where switching branches could
    /// carry or clash with the person's work, so it's asked about; they're never touched, and "Continue" with changes still
    /// there asks again. A clean clone on another branch (e.g. the previous issue's, under review) is just switched: its
    /// commits stay on that branch.
    /// </summary>
    private static bool CheckoutsReady(SpaceContext space, AgentRunSummary run)
    {
        while (true)
        {
            var problems = Checkouts.Problems(space, WorkspaceManager.BranchName(space, run.IssueKey, run.IssueTitle))
                .Where(x => x.HasChanges)
                .ToList();
            if (problems.Count == 0)
            {
                return true;
            }

            Log.Info($"{run.IssueKey}: asking before switching the clones - {string.Join("; ", problems.Select(x => x.Text))}.");
            var message = $"""
                {run.IssueKey} "{run.IssueTitle}" is ready for the agent, which will work in your clones on branch {WorkspaceManager.BranchName(space, run.IssueKey, run.IssueTitle)}.

                {string.Join("\n", problems.Select(x => x.Text))}

                Commit or stash the changes first - they're never touched. Continue?
                """;
            if (!UserPrompt.Ask(message, yes: "Continue", no: "Not now"))
            {
                return false;
            }
        }
    }
}
