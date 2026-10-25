using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Commands;

/// <summary>`lba`: polls Boards for the person's Ready runs and works on them one at a time, until stopped.</summary>
static class RunCommand
{
    public static async Task<int> RunAsync(RunnerArgs options, CancellationToken ct)
    {
        var config = RunnerConfig.Load(options.ConfigPath);
        var codingAgent = CodingAgents.For(config);
        if (options.Check)
        {
            Console.WriteLine($"laraue-boards-agent {RunnerVersion.Value}");
            Console.WriteLine($"Config: {options.ConfigPath}");
            Console.WriteLine($"{codingAgent.Name}: {codingAgent.FindExecutable()}");
            Console.WriteLine($"Agent skill: {AgentPlugin.Directory.Value}");
        }

        var apiKey = ApiKeySource.Resolve(config);
        if (apiKey is null)
        {
            Log.Error("No Boards API key: run `lba setup` to sign in.");
            return 1;
        }

        await using var boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey, ct);

        var me = await boards.GetMe(ct);
        Log.Info($"Connected to {config.McpUrl} as {me.User.DisplayName} ({me.User.Id}), organization {me.Organization.Name}.");
        if (options.Check)
        {
            return 0;
        }

        Log.Info($"Watching your Ready runs in every space; repositories are looked up under {string.Join(", ", config.CodeFolders)}."
            + (options.DryRun ? " Dry run - nothing is claimed." : $" {codingAgent.Name}: {codingAgent.FindExecutable()}"));

        var spaces = new SpaceResolver(boards, config, new CloneIndex(config.CodeFolders));
        var runner = new Runner(boards, config, codingAgent, apiKey, RunsInProgress.PathFor(options.ConfigPath), options.ConfigPath);
        // Runs skipped for having no repository here, so each is logged once rather than every poll.
        var skippedRuns = new HashSet<long>();
        // Checkout mode: runs the person said "Not now" to, and when to ask about them again.
        var postponedRuns = new Dictionary<long, DateTime>();
        // Boards not answering (a dropped connection, a deploy) is logged and retried at the next poll. Several failures in a row
        // end the process, so the service restarts it with a fresh connection - an MCP session doesn't survive a server restart.
        const int MaxFailuresInARow = 5;
        var failuresInARow = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var worked = false;
                try
                {
                    if (!options.DryRun)
                    {
                        await runner.RecoverInterruptedRun(ct);
                    }

                    var runs = await boards.ListReadyRuns(spaceKey: null, me.User.Id, ct);
                    var resolved = new Dictionary<string, SpaceContext>();
                    foreach (var run in runs)
                    {
                        var spaceKey = IssueKeys.SpaceKey(run.IssueKey);
                        if (!resolved.TryGetValue(spaceKey, out var space))
                        {
                            space = await spaces.Resolve(spaceKey, ct);
                            resolved[spaceKey] = space;
                        }

                        if (space.Repos.Count == 0)
                        {
                            if (skippedRuns.Add(run.Id))
                            {
                                var links = space.OtherLinks.Count == 0 ? "the space has no links" : $"links: {string.Join(", ", space.OtherLinks.Select(x => x.Url))}";
                                Log.Info($"{run.IssueKey}: Ready, but none of {spaceKey}'s repositories is cloned under {string.Join(", ", config.CodeFolders)} ({links}) - left for another machine.");
                            }

                            continue;
                        }

                        if (postponedRuns.TryGetValue(run.Id, out var askAgainAt) && DateTime.UtcNow < askAgainAt)
                        {
                            continue;
                        }

                        Log.Info($"{spaceKey}: Ready run {run.Id} for {run.IssueKey} \"{run.IssueTitle}\" (handed off by {run.CreatedBy} at {run.CreatedAt:u}), repositories: {string.Join(", ", space.Repos.Select(x => x.Name))}.");
                        if (!options.DryRun && config.InCheckouts && !CheckoutsReady(space, run))
                        {
                            postponedRuns[run.Id] = DateTime.UtcNow.AddMinutes(10);
                            Log.Info($"{run.IssueKey}: not now - asking again in 10 minutes.");
                            continue;
                        }

                        postponedRuns.Remove(run.Id);
                        if (!options.DryRun && await runner.TryProcess(space, run, ct))
                        {
                            // One run at a time: poll again from the start once it's over.
                            worked = true;
                            break;
                        }
                    }

                    failuresInARow = 0;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    failuresInARow++;
                    if (failuresInARow >= MaxFailuresInARow)
                    {
                        Log.Error($"Boards failed {failuresInARow} times in a row ({e.Message}) - exiting so the service starts again with a new connection.");
                        return 1;
                    }

                    Log.Error($"Couldn't reach Boards ({e.GetType().Name}: {e.Message}) - trying again in {config.PollIntervalSeconds} s.");
                }

                if (options.Once)
                {
                    break;
                }

                if (!worked)
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        Log.Info("Stopped.");
        return 0;
    }

    // Checkout mode: true once no clone of the space has uncommitted changes - the one case where switching branches could
    // carry or clash with the person's work, so it's asked about; they're never touched, and "Continue" with changes still
    // there asks again. A clean clone on another branch (e.g. the previous issue's, under review) is just switched: its
    // commits stay on that branch.
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
