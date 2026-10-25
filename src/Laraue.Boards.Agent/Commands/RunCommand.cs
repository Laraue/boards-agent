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

        var runner = new Runner(boards, config, codingAgent, apiKey, RunsInProgress.PathFor(options.ConfigPath), options.ConfigPath);
        var poller = new ReadyRunPoller(
            boards,
            config,
            new SpaceResolver(boards, config, new CloneIndex(config.CodeFolders)),
            runner,
            me.User.Id,
            options.DryRun);
        return await PollUntilStopped(poller, config, options.Once, ct);
    }

    /// <summary>
    /// Boards not answering (a dropped connection, a deploy) is logged and retried at the next poll. Several failures in a
    /// row end the process, so the service restarts it with a fresh connection - an MCP session doesn't survive a server
    /// restart.
    /// </summary>
    private const int MaxFailuresInARow = 5;

    /// <summary>Polls until stopped (or once, with --once); right after a run is worked on, polls again without waiting.</summary>
    private static async Task<int> PollUntilStopped(ReadyRunPoller poller, RunnerConfig config, bool once, CancellationToken ct)
    {
        var failuresInARow = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var worked = false;
                try
                {
                    worked = await poller.PollOnce(ct);
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

                if (once)
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
}
