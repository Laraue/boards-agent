using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Commands;

/// <summary>
/// `lba continue KEY ["instruction"]` (BRD-317): keep working on an issue the agent ran here, without the Boards round
/// trip. Puts the workspace back on the issue's branch, resumes the agent's session - in this terminal, or for one
/// unattended round with the instruction - and afterwards posts one summary comment (and publishes, if set up); see
/// <see cref="ContinueSession"/>.
/// </summary>
static class ContinueCommand
{
    public static async Task<int> RunAsync(RunnerArgs options, CancellationToken ct)
    {
        var config = RunnerConfig.Load(options.ConfigPath);
        var runFile = RunsInProgress.PathFor(options.ConfigPath);
        if (FindSession(options.IssueKey, runFile) is not { } session)
        {
            return 1;
        }

        var codingAgent = CodingAgents.All(config).FirstOrDefault(x => x.Key == session.Agent);
        if (codingAgent is null)
        {
            Console.WriteLine($"{session.IssueKey} was run by \"{session.Agent}\", which this version doesn't know.");
            return 1;
        }

        if (ApiKeySource.Resolve(config) is not { } apiKey)
        {
            Console.WriteLine("No Boards API key: run `lba setup` to sign in.");
            return 1;
        }

        var workspace = session.Workspace.ToWorkspace(session.IssueKey);
        if (!Prepare(workspace))
        {
            return 1;
        }

        await using var continued = await ContinueSession.Open(config, runFile, session, codingAgent, apiKey, workspace, ct);
        return await continued.Run(options.Instruction, ct);
    }

    /// <summary>The agent session of the issue named, or picked (<see cref="PickIssue"/>); null, said why, when there's none.</summary>
    private static AgentSession? FindSession(string? issueKey, string runFile)
    {
        var key = issueKey ?? PickIssue(runFile);
        if (key is null)
        {
            Console.WriteLine("No agent run on this computer to continue yet. Hand an issue to the agent in Boards first.");
            return null;
        }

        if (AgentSessions.Load(runFile, key) is not { } session)
        {
            Console.WriteLine($"There's no agent run of {key} on this computer to continue. Hand it to the agent in Boards first.");
            return null;
        }

        return session;
    }

    /// <summary>
    /// The issue when `lba continue` gets no key: the one whose agent branch the current folder is on
    /// (agent/BRD-42-...), else the one this computer's agent worked on last. Says which it picked.
    /// </summary>
    private static string? PickIssue(string runFile)
    {
        if (Git.FindCheckout(Environment.CurrentDirectory) is { } checkout
            && Git.CurrentBranch(checkout) is { } branch
            && System.Text.RegularExpressions.Regex.Match(branch, "^agent/([A-Za-z][A-Za-z0-9]*-[0-9]+)") is { Success: true } match
            && AgentSessions.Load(runFile, match.Groups[1].Value) is not null)
        {
            var fromBranch = match.Groups[1].Value.ToUpperInvariant();
            Console.WriteLine($"Continuing {fromBranch}, the issue of the branch you're on ({branch}).");
            return fromBranch;
        }

        if (AgentSessions.Latest(runFile) is { } latest)
        {
            Console.WriteLine($"Continuing {latest.IssueKey} \"{latest.IssueTitle}\", your agent's last run. Name another issue with `lba continue <issue key>`.");
            return latest.IssueKey;
        }

        return null;
    }

    /// <summary>
    /// The issue's branch checked out again: in checkout mode each clone switched to it (asking first about uncommitted
    /// changes, which are never touched); in worktree mode a removed worktree made again. A repository whose empty
    /// branch was deleted after the run gets it back from its base.
    /// </summary>
    internal static bool Prepare(Workspace workspace)
    {
        foreach (var repo in workspace.Repos)
        {
            if (!workspace.InCheckouts)
            {
                if (!Directory.Exists(repo.Path))
                {
                    Git.CreateWorktree(repo.ClonePath, repo.BaseRef, repo.Path, workspace.Branch);
                }

                continue;
            }

            if (Git.CurrentBranch(repo.Path) == workspace.Branch)
            {
                continue;
            }

            while (Git.UncommittedFiles(repo.Path) is var uncommitted and not 0)
            {
                if (!UserPrompt.Ask(
                        $"{repo.Name} ({repo.Path}) has {(uncommitted < 0 ? "a git status that can't be read" : $"{uncommitted} uncommitted file(s)")}. Commit or stash them to switch it to {workspace.Branch}. Continue?",
                        yes: "Continue",
                        no: "Stop"))
                {
                    return false;
                }
            }

            Git.SwitchToBranch(repo.Path, repo.BaseRef, workspace.Branch);
            Console.WriteLine($"{repo.Name}: switched to {workspace.Branch}.");
        }

        return true;
    }
}
