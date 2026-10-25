using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Publishing;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Commands;

/// <summary>
/// `lba continue KEY ["instruction"]` (BRD-317): keep working on an issue the agent ran here, without the Boards round
/// trip. Puts the workspace back on the issue's branch, resumes the agent's session - in this terminal, or for one
/// unattended round with the instruction - and afterwards posts one summary comment (and publishes, if set up).
/// </summary>
static class ContinueCommand
{
    public static async Task<int> RunAsync(RunnerArgs options, CancellationToken ct)
    {
        var config = RunnerConfig.Load(options.ConfigPath);
        var runFile = RunsInProgress.PathFor(options.ConfigPath);
        var key = options.IssueKey ?? PickIssue(runFile);
        if (key is null)
        {
            Console.WriteLine("No agent run on this computer to continue yet. Hand an issue to the agent in Boards first.");
            return 1;
        }

        if (AgentSessions.Load(runFile, key) is not { } session)
        {
            Console.WriteLine($"There's no agent run of {key} on this computer to continue. Hand it to the agent in Boards first.");
            return 1;
        }

        var codingAgent = CodingAgents.All(config).FirstOrDefault(x => x.Key == session.Agent);
        if (codingAgent is null)
        {
            Console.WriteLine($"{key} was run by \"{session.Agent}\", which this version doesn't know.");
            return 1;
        }

        if (ApiKeySource.Resolve(config) is not { } apiKey)
        {
            Console.WriteLine("No Boards API key: run `lba setup` to sign in.");
            return 1;
        }

        var workspace = session.Workspace.ToWorkspace(key);
        if (!Prepare(workspace))
        {
            return 1;
        }

        // The session is an agent run in Boards (BRD-320), so the issue shows the agent working on it, then done.
        BoardsClient? boards = null;
        AgentRunSummary? continueRun = null;
        int? commentsBefore = null;
        try
        {
            boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey, ct);
            commentsBefore = (await boards.GetIssue(key, ct)).CommentCount;
            continueRun = await OpenRun(boards, runFile, session, ct);
            if (continueRun is not null)
            {
                session = session with { ContinueRunId = continueRun.Id };
                AgentSessions.Save(runFile, session);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"Couldn't start an agent run in Boards ({e.Message}) - continuing without one.");
        }

        var startHeads = workspace.Repos.ToDictionary(x => x.Path, x => Git.ResolveCommit(x.Path, "HEAD"));
        var stopped = false;
        if (options.Instruction is { } instruction)
        {
            var logPath = Path.Combine(config.WorktreeFolder, $"{key}.continue-{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl");
            Console.WriteLine($"{codingAgent.Name} continues {key} on {workspace.Branch}: \"{instruction}\". Log: {logPath}");
            using var agent = codingAgent.Resume(apiKey, key, workspace, session.SessionId, Prompt(key, instruction, continueRun), logPath);
            agent.OnStep = step => Console.WriteLine($"  {DateTime.Now:HH:mm:ss}  {step}");
            var finished = await Task.WhenAny(agent.WaitForExitAsync(), Task.Delay(Timeout.Infinite, ct));
            if (finished is not Task { IsCompletedSuccessfully: true } || ct.IsCancellationRequested)
            {
                agent.Kill();
                stopped = true;
                Console.WriteLine("Stopped.");
            }

            // Its answer belongs on the screen, not only in the log - e.g. something it was asked to print.
            if (agent.FinalMessage() is { Length: > 0 } answer)
            {
                Console.WriteLine();
                Console.WriteLine(answer);
                Console.WriteLine();
            }

            Console.WriteLine(agent.Summary());
            if (agent.SessionId is { } sessionId && sessionId != session.SessionId)
            {
                session = session with { SessionId = sessionId };
                AgentSessions.Save(runFile, session);
            }
        }
        else
        {
            Console.WriteLine($"Opening {codingAgent.Name}'s session of {key} on {workspace.Branch}. Exit it when you're done; laraue-boards-agent then posts a summary to the issue.");
            codingAgent.ResumeInteractive(apiKey, key, workspace, session.SessionId, continueRun is null ? null : RunContext(key, continueRun));
        }

        var changed = workspace.Repos
            .Select(repo => new RepoCommits(repo, startHeads[repo.Path] is { } head ? Git.CommitsSince(repo.Path, head, workspace.Branch) : -1))
            .Where(x => x.Commits > 0)
            .ToList();

        // As after a run: repositories the agent never committed to go back where they were, their empty branch deleted.
        WorkspaceManager.Finish(workspace);

        try
        {
            var artifacts = boards is not null && commentsBefore is { } before
                ? await Artifacts(boards, key, before)
                : [];
            return await Report(config, options, codingAgent, apiKey, session, workspace, changed, artifacts, boards, continueRun, stopped);
        }
        finally
        {
            if (boards is not null)
            {
                await boards.DisposeAsync();
            }
        }
    }

    private sealed record RepoCommits(WorkspaceRepo Repo, int Commits);

    /// <summary>
    /// The files the agent attached to the issue during the session (BRD-318): the attachments of comments after the
    /// first <paramref name="commentsBefore"/> written through an API key - the agent's, since the session's key is the
    /// person's own. Empty when they can't be read: the summary then goes by commits alone.
    /// </summary>
    private static async Task<IReadOnlyList<string>> Artifacts(BoardsClient boards, string key, int commentsBefore)
    {
        try
        {
            var comments = await boards.ListIssueCommentsFrom(key, commentsBefore, CancellationToken.None);
            return comments
                .Where(x => x.CanManage && x.ApiKeyName is not null)
                .SelectMany(x => x.Attachments)
                .Select(x => x.FileName ?? x.Id.ToString())
                .ToList();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't read the agent's comments on {key} ({e.Message}) - only new commits count.");
            return [];
        }
    }

    /// <summary>
    /// After the session: the summary comment when there are new commits or artifacts, publishing when there are new
    /// commits, and the end of the session's run. Ctrl+C doesn't stop it: in an interactive session it was meant for the
    /// agent, and a run left Working would keep the issue locked.
    /// </summary>
    private static async Task<int> Report(
        RunnerConfig config,
        RunnerArgs options,
        ICodingAgent codingAgent,
        string apiKey,
        AgentSession session,
        Workspace workspace,
        IReadOnlyList<RepoCommits> changed,
        IReadOnlyList<string> artifacts,
        BoardsClient? boards,
        AgentRunSummary? continueRun,
        bool stopped)
    {
        var key = session.IssueKey;
        if (changed.Count == 0 && artifacts.Count == 0)
        {
            Console.WriteLine("No new commits or artifacts, so nothing is posted to Boards.");
            if (continueRun is not null && boards is not null)
            {
                await EndRun(boards, key, continueRun, done: false);
            }

            return 0;
        }

        var run = new AgentRunSummary(0, key, session.IssueTitle, session.IssueUrl, AgentRunStates.Done, DateTime.UtcNow, "", null, null, null, null, false);
        // Only new commits are pushed: a round that only produced an artifact leaves the branch as it was.
        var published = changed.Count == 0 ? [] : Publisher.Publish(config, run, workspace);
        var comment = $"_laraue-boards-agent: continued with {codingAgent.Name} on {Environment.MachineName}"
            + (options.Instruction is { } asked ? $" (\"{Shorten(asked)}\")" : "")
            + "_"
            + (changed.Count == 0
                ? ""
                : "\n\nNew commits:\n" + string.Join("\n", changed.Select(x => $"- {x.Repo.Name}: `{workspace.Branch}`, {x.Commits} commit{(x.Commits == 1 ? "" : "s")}")))
            + (artifacts.Count == 0 ? "" : "\n\nArtifacts, attached to the agent's comments above:\n" + string.Join("\n", artifacts.Select(x => $"- `{x}`")))
            + (published.Count == 0 ? "" : "\n\nPublished:\n" + string.Join("\n", published.Select(x => $"- {x.Line}")));
        try
        {
            if (boards is null)
            {
                await using var connected = await BoardsClient.ConnectAsync(config.McpUrl, apiKey, CancellationToken.None);
                await connected.CreateComment(key, comment, CancellationToken.None);
            }
            else
            {
                await boards.CreateComment(key, comment, CancellationToken.None);
            }

            Console.WriteLine($"Posted a summary to {key}: {changed.Sum(x => x.Commits)} new commit(s), {artifacts.Count} artifact(s).");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't post the summary to Boards: {e.Message}");
        }

        // A stopped round may be half done: its commits stay on the branch, but the run isn't reported as done.
        if (continueRun is not null && boards is not null)
        {
            await EndRun(boards, key, continueRun, done: !stopped);
        }

        var pullRequest = published.Select(x => x.PullRequestUrl).OfType<string>().FirstOrDefault();
        Notifications.Show(config, $"{key} · ready for review", pullRequest is null ? session.IssueTitle : $"{session.IssueTitle} - {pullRequest}", session.IssueUrl);
        return 0;
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

    /// <summary>
    /// The session's run: a new Working one (start_agent_run), or the one an earlier `lba continue` of this issue
    /// started and didn't end (it was cut off). Null when the issue has another open run - a hand-off, or a runner on
    /// it - which this session leaves alone.
    /// </summary>
    private static async Task<AgentRunSummary?> OpenRun(BoardsClient boards, string runFile, AgentSession session, CancellationToken ct)
    {
        var key = session.IssueKey;
        var issue = await boards.GetIssue(key, ct);
        if (issue.LatestAgentRun is { State: AgentRunStates.Ready or AgentRunStates.Working } open)
        {
            if (open.Id == session.ContinueRunId && open.State == AgentRunStates.Working)
            {
                Console.WriteLine($"Carrying on with agent run {open.Id}, which an earlier `lba continue` of {key} left open.");
                return open;
            }

            Console.WriteLine($"{key} already has an open agent run ({open.Id}, {open.State}), so this session doesn't start one.");
            return null;
        }

        var run = await boards.StartAgentRun(key, ct);
        Console.WriteLine($"Started agent run {run.Id}: {key} shows the agent working until this session ends.");
        return run;
    }

    /// <summary>
    /// Ends the session's run: with new commits or artifacts (<paramref name="done"/>) the issue goes to Review, if it wasn't there or
    /// further yet, and the run is finished Done; without, the run is cancelled, so a look around leaves nothing but its
    /// history. A run the agent or a person already ended is left as it is. Not cancellable.
    /// </summary>
    private static async Task EndRun(BoardsClient boards, string key, AgentRunSummary run, bool done)
    {
        try
        {
            var issue = await boards.GetIssue(key, CancellationToken.None);
            var state = issue.LatestAgentRun?.Id == run.Id ? issue.LatestAgentRun.State : null;
            if (!done)
            {
                if (state == AgentRunStates.Working && await boards.CancelAgentRun(run.Id, CancellationToken.None))
                {
                    Console.WriteLine($"Cancelled agent run {run.Id}: nothing to review.");
                }

                return;
            }

            if (state is not (AgentRunStates.Working or AgentRunStates.Done))
            {
                Console.WriteLine($"Agent run {run.Id} is {state ?? "no longer the issue's latest"}, so it's left as it is.");
                return;
            }

            if (issue.StatusCategoryId is StatusCategories.Created or StatusCategories.InProgress
                && (await boards.ListStatuses(IssueKeys.SpaceKey(key), CancellationToken.None))
                    .FirstOrDefault(x => x.EpicId == issue.EpicId)?.Statuses
                    .FirstOrDefault(x => x.CategoryId == StatusCategories.Review) is { } review)
            {
                await boards.EditIssueStatus(key, review.Id, null, CancellationToken.None);
                Console.WriteLine($"Moved {key} to {review.Name}.");
            }

            if (state == AgentRunStates.Working && await boards.FinishAgentRun(run.Id, AgentRunStates.Done, null, CancellationToken.None))
            {
                Console.WriteLine($"Finished agent run {run.Id} as Done.");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't end agent run {run.Id} in Boards ({e.Message}) - `lba continue {key}` carries on with it, or cancel it in Boards.");
        }
    }

    /// <summary>What the agent is told about the session's run, so its finish_agent_run - if any - is this run's.</summary>
    private static string RunContext(string issueKey, AgentRunSummary run)
    {
        return $"""
            This session continues your work on {issueKey} as agent run {run.Id} (agent run id: {run.Id}) - any earlier run id
            of the issue is over. Commit your changes on the branch as before. When the session ends, laraue-boards-agent
            posts the summary, moves the issue to review and finishes run {run.Id} (Done with new commits or artifacts,
            cancelled without), so don't change the issue's status or finish the run yourself - unless you give up: then
            finish_agent_run({run.Id}, Failed, reason). {ArtifactsContext}
            """;
    }

    /// <summary>The summary lists only commits, so an artifact of the session is the agent's to attach (BRD-318).</summary>
    private const string ArtifactsContext =
        "Attach any artifact you're asked to produce or print (a config, a dashboard JSON, a report) to the issue yourself, "
        + "as the work-on-issue skill's \"Artifacts\" says: create_comment with files.";

    private static string Prompt(string issueKey, string instruction, AgentRunSummary? run)
    {
        var context = run is null
            ? $"""
              This continues your work on {issueKey} after its run, outside Boards: commit your changes on the branch as before.
              Don't change the issue's status or finish an agent run in Boards - laraue-boards-agent posts the summary.
              {ArtifactsContext}
              """
            : RunContext(issueKey, run);
        return $"""
            {instruction}

            ({context})
            """;
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 120 ? line : line[..117] + "...";
    }
}
