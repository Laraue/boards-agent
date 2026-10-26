using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Publishing;

/// <summary>What the runner asks GitHub through the GitHub CLI, signed in as the person. Never the agent: it can't run gh.</summary>
public static class GitHub
{
    /// <summary>
    /// Whether the newest pull request of <paramref name="branch"/> was merged - which a squash merge only shows here, not
    /// in git. False without gh, a pull request or GitHub access.
    /// </summary>
    public static bool HasMergedPullRequest(string repo, string branch)
    {
        if (CodingAgents.FindOnPath("gh") is not { } gh)
        {
            return false;
        }

        var result = Publisher.RunGh(gh, repo, ["pr", "list", "--head", branch, "--state", "all", "--limit", "1", "--json", "state", "--jq", ".[0].state"]);
        if (result.ExitCode != 0)
        {
            Log.Info($"{repo}: couldn't ask GitHub about {branch}'s pull requests: {result.Error.Trim()}");
            return false;
        }

        return result.Output.Trim() == "MERGED";
    }
}
