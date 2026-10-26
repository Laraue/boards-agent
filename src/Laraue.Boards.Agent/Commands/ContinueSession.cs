using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Publishing;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Commands;

/// <summary>
/// One `lba continue` of an issue, once its branch is checked out: the session is an agent run in Boards (BRD-320), so
/// the issue shows the agent working on it, then done. Boards being unreachable doesn't stop it: the session goes on
/// without a run, and the summary is posted if Boards answers by then.
/// </summary>
public sealed class ContinueSession : IAsyncDisposable
{
    private readonly RunnerConfig _config;
    private readonly string _runFile;
    private readonly ICodingAgent _codingAgent;
    private readonly string _apiKey;
    private readonly Workspace _workspace;
    private readonly BoardsClient? _boards;
    private readonly AgentRunSummary? _run;
    private readonly int? _commentsBefore;
    private AgentSession _session;

    private ContinueSession(
        RunnerConfig config,
        string runFile,
        AgentSession session,
        ICodingAgent codingAgent,
        string apiKey,
        Workspace workspace,
        BoardsClient? boards,
        AgentRunSummary? run,
        int? commentsBefore)
    {
        _config = config;
        _runFile = runFile;
        _session = session;
        _codingAgent = codingAgent;
        _apiKey = apiKey;
        _workspace = workspace;
        _boards = boards;
        _run = run;
        _commentsBefore = commentsBefore;
    }

    private string Key => _session.IssueKey;

    /// <summary>Connects to Boards and opens the session's run (<see cref="OpenRun"/>), remembering how many comments the issue had.</summary>
    public static async Task<ContinueSession> Open(
        RunnerConfig config,
        string runFile,
        AgentSession session,
        ICodingAgent codingAgent,
        string apiKey,
        Workspace workspace,
        CancellationToken ct)
    {
        BoardsClient? boards = null;
        AgentRunSummary? run = null;
        int? commentsBefore = null;
        try
        {
            boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey, ct);
            commentsBefore = (await boards.GetIssue(session.IssueKey, ct)).CommentCount;
            run = await OpenRun(boards, session, ct);
            if (run is not null)
            {
                session = session with { ContinueRunId = run.Id };
                AgentSessions.Save(runFile, session);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"Couldn't start an agent run in Boards ({e.Message}) - continuing without one.");
        }

        return new ContinueSession(config, runFile, session, codingAgent, apiKey, workspace, boards, run, commentsBefore);
    }

    /// <summary>
    /// The agent's session resumed - one unattended round with <paramref name="instruction"/>, or interactively in this
    /// terminal - then what it left reported.
    /// </summary>
    public async Task<int> Run(string? instruction, CancellationToken ct)
    {
        var startHeads = _workspace.Repos.ToDictionary(x => x.Path, x => Git.ResolveCommit(x.Path, "HEAD"));
        var stopped = false;
        if (instruction is not null)
        {
            stopped = await RunInstruction(instruction, ct);
        }
        else
        {
            Console.WriteLine($"Opening {_codingAgent.Name}'s session of {Key} on {_workspace.Branch}. Exit it when you're done; laraue-boards-agent then posts a summary to the issue.");
            _codingAgent.ResumeInteractive(_apiKey, Key, _workspace, _session.SessionId, _run is null ? null : RunContext(Key, _run));
        }

        var changed = NewCommits(startHeads);

        // As after a run: repositories the agent never committed to go back where they were, their empty branch deleted.
        WorkspaceManager.Finish(_workspace);

        var artifacts = _boards is not null && _commentsBefore is { } before
            ? await Artifacts(_boards, Key, before)
            : [];
        return await Report(instruction, changed, artifacts, stopped);
    }

    /// <summary>One unattended round with the instruction, its steps and answer printed. True when it was stopped (Ctrl+C).</summary>
    private async Task<bool> RunInstruction(string instruction, CancellationToken ct)
    {
        var stopped = false;
        var logPath = Path.Combine(_config.WorktreeFolder, $"{Key}.continue-{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl");
        Console.WriteLine($"{_codingAgent.Name} continues {Key} on {_workspace.Branch}: \"{instruction}\". Log: {logPath}");
        using var agent = _codingAgent.Resume(_apiKey, Key, _workspace, _session.SessionId, Prompt(Key, instruction, _run), logPath);
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
        if (agent.SessionId is { } sessionId && sessionId != _session.SessionId)
        {
            _session = _session with { SessionId = sessionId };
            AgentSessions.Save(_runFile, _session);
        }

        return stopped;
    }

    private sealed record RepoCommits(WorkspaceRepo Repo, int Commits);

    /// <summary>The repositories whose branch got commits during the session, and how many.</summary>
    private IReadOnlyList<RepoCommits> NewCommits(Dictionary<string, string?> startHeads)
    {
        return _workspace.Repos
            .Select(repo => new RepoCommits(repo, startHeads[repo.Path] is { } head ? Git.CommitsSince(repo.Path, head, _workspace.Branch) : -1))
            .Where(x => x.Commits > 0)
            .ToList();
    }

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
    private async Task<int> Report(string? instruction, IReadOnlyList<RepoCommits> changed, IReadOnlyList<string> artifacts, bool stopped)
    {
        if (changed.Count == 0 && artifacts.Count == 0)
        {
            Console.WriteLine("No new commits or artifacts, so nothing is posted to Boards.");
            if (_run is not null && _boards is not null)
            {
                await EndRun(_boards, Key, _run, done: false);
            }

            return 0;
        }

        var run = new AgentRunSummary(0, Key, _session.IssueTitle, _session.IssueUrl, AgentRunStates.Done, DateTime.UtcNow, "", null, null, null, null, false);
        // Only new commits are pushed: a round that only produced an artifact leaves the branch as it was.
        var published = changed.Count == 0 ? [] : Publisher.Publish(_config, run, _workspace);
        await PostSummary(SummaryComment(instruction, changed, artifacts, published), changed.Sum(x => x.Commits), artifacts.Count);

        // A stopped round may be half done: its commits stay on the branch, but the run isn't reported as done.
        if (_run is not null && _boards is not null)
        {
            await EndRun(_boards, Key, _run, done: !stopped);
        }

        var pullRequest = published.Select(x => x.PullRequestUrl).OfType<string>().FirstOrDefault();
        Notifications.Show(_config, $"{Key} · ready for review", pullRequest is null ? _session.IssueTitle : $"{_session.IssueTitle} - {pullRequest}", _session.IssueUrl);
        return 0;
    }

    private string SummaryComment(
        string? instruction,
        IReadOnlyList<RepoCommits> changed,
        IReadOnlyList<string> artifacts,
        IReadOnlyList<PublishedRepo> published)
    {
        return $"_laraue-boards-agent: continued with {_codingAgent.Name} on {Environment.MachineName}"
            + (instruction is not null ? $" (\"{Shorten(instruction)}\")" : "")
            + "_"
            + (changed.Count == 0
                ? ""
                : "\n\nNew commits:\n" + string.Join("\n", changed.Select(x => $"- {x.Repo.Name}: `{_workspace.Branch}`, {x.Commits} commit{(x.Commits == 1 ? "" : "s")}")))
            + (artifacts.Count == 0 ? "" : "\n\nArtifacts, attached to the agent's comments above:\n" + string.Join("\n", artifacts.Select(x => $"- `{x}`")))
            + (published.Count == 0 ? "" : "\n\nPublished:\n" + string.Join("\n", published.Select(x => $"- {x.Line}")));
    }

    /// <summary>Posts the summary, connecting again if Boards wasn't reachable when the session started.</summary>
    private async Task PostSummary(string comment, int commits, int artifacts)
    {
        try
        {
            if (_boards is null)
            {
                await using var connected = await BoardsClient.ConnectAsync(_config.McpUrl, _apiKey, CancellationToken.None);
                await connected.CreateComment(Key, comment, CancellationToken.None);
            }
            else
            {
                await _boards.CreateComment(Key, comment, CancellationToken.None);
            }

            Console.WriteLine($"Posted a summary to {Key}: {commits} new commit(s), {artifacts} artifact(s).");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't post the summary to Boards: {e.Message}");
        }
    }

    /// <summary>
    /// The session's run: a new Working one (start_agent_run), or the one an earlier `lba continue` of this issue
    /// started and didn't end (it was cut off). Null when the issue has another open run - a hand-off, or a runner on
    /// it - which this session leaves alone.
    /// </summary>
    private static async Task<AgentRunSummary?> OpenRun(BoardsClient boards, AgentSession session, CancellationToken ct)
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

    public async ValueTask DisposeAsync()
    {
        if (_boards is not null)
        {
            await _boards.DisposeAsync();
        }
    }
}
