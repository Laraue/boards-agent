using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Publishing;

/// <summary>One repository's published work: a line for the issue comment, and the pull request, if one was opened.</summary>
public sealed record PublishedRepo(string Line, string? PullRequestUrl);

/// <summary>
/// After a Done run, publishes the agent's work as <see cref="RunnerConfig.Publish"/> says (BRD-315): pushes the issue's
/// branch in every repository the agent committed to and, with <see cref="PublishModes.PullRequest"/>, opens a pull
/// request with the GitHub CLI (or finds the one an earlier run opened). Done by the runner, never the agent. Failures
/// are reported in the returned lines, not thrown: the run is done either way.
/// </summary>
public static class Publisher
{
    public static IReadOnlyList<PublishedRepo> Publish(RunnerConfig config, AgentRunSummary run, Workspace workspace)
    {
        var mode = config.Publish.ToLowerInvariant();
        if (mode == PublishModes.Off)
        {
            return [];
        }

        var gh = mode == PublishModes.PullRequest ? CodingAgents.FindOnPath("gh") : null;
        var published = new List<PublishedRepo>();
        foreach (var repo in workspace.Repos)
        {
            if (repo.BaseCommit is null || Git.CommitsSince(repo.Path, repo.BaseCommit, workspace.Branch) <= 0)
            {
                continue;
            }

            var push = Git.Push(repo.Path, workspace.Branch);
            if (push.ExitCode != 0)
            {
                Log.Error($"{run.IssueKey}: pushing {workspace.Branch} in {repo.Name} failed: {push.Error.Trim()}");
                published.Add(new PublishedRepo($"{repo.Name}: pushing `{workspace.Branch}` failed: {FirstLine(push.Error)}", null));
                continue;
            }

            Log.Info($"{run.IssueKey}: pushed {workspace.Branch} in {repo.Name}.");
            if (mode != PublishModes.PullRequest)
            {
                published.Add(new PublishedRepo($"{repo.Name}: pushed `{workspace.Branch}`", null));
            }
            else if (gh is null)
            {
                published.Add(new PublishedRepo($"{repo.Name}: pushed `{workspace.Branch}` - no pull request: the GitHub CLI (gh) isn't installed", null));
            }
            else
            {
                var url = OpenPullRequest(gh, run, workspace, repo, out var error);
                published.Add(url is null
                    ? new PublishedRepo($"{repo.Name}: pushed `{workspace.Branch}` - the pull request failed: {error}", null)
                    : new PublishedRepo($"{repo.Name}: {url}", url));
            }
        }

        return published;
    }

    /// <summary>The pull request of the issue's branch: a new one, or the one an earlier run opened. Null on failure.</summary>
    private static string? OpenPullRequest(string gh, AgentRunSummary run, Workspace workspace, WorkspaceRepo repo, out string error)
    {
        var slash = repo.BaseRef.IndexOf('/');
        var baseBranch = slash > 0 ? repo.BaseRef[(slash + 1)..] : repo.BaseRef;
        var create = RunGh(
            gh,
            repo.Path,
            [
                "pr", "create",
                "--head", workspace.Branch,
                "--base", baseBranch,
                "--title", $"{run.IssueKey}: {run.IssueTitle}",
                "--body", $"{run.IssueUrl}\n\nWork of the agent run on {run.IssueKey}, opened by laraue-boards-agent. The agent's summary is in the issue's comments.",
            ]);
        if (create.ExitCode == 0 && LastUrl(create.Output) is { } created)
        {
            Log.Info($"{run.IssueKey}: opened {created}.");
            error = "";
            return created;
        }

        // A re-run of the issue: its branch already has a pull request.
        var existing = RunGh(gh, repo.Path, ["pr", "view", workspace.Branch, "--json", "url", "--jq", ".url"]);
        if (existing.ExitCode == 0 && LastUrl(existing.Output) is { } url)
        {
            error = "";
            return url;
        }

        error = FirstLine(create.Error.Length > 0 ? create.Error : create.Output);
        Log.Error($"{run.IssueKey}: gh pr create in {repo.Name} failed: {create.Error.Trim()}");
        return null;
    }

    public static ProcessResult RunGh(string gh, string folder, string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(gh) { WorkingDirectory = folder };
        // Nobody can answer a prompt in the background service.
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Shell.Run(info);
    }

    private static string? LastUrl(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(x => x.StartsWith("https://", StringComparison.Ordinal));
    }

    private static string FirstLine(string text)
    {
        return text.Trim().Split('\n')[0].Trim();
    }
}
