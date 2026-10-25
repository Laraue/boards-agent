using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Commands;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Publishing;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Runs;

/// <summary>Takes one run from claim to finish.</summary>
/// <param name="runFilePath">Where the run in progress is recorded (<see cref="RunsInProgress"/>).</param>
sealed class Runner(BoardsClient boards, RunnerConfig config, ICodingAgent codingAgent, string apiKey, string runFilePath, string configPath)
{
    private const int MaxReasonLength = 4096;

    /// <summary>Claims the run and works on it. False when it couldn't be claimed (taken, cancelled or blocked).</summary>
    public async Task<bool> TryProcess(SpaceContext space, AgentRunSummary run, CancellationToken ct)
    {
        if (!await boards.ClaimAgentRun(run.Id, ct))
        {
            Log.Info($"{run.IssueKey}: run {run.Id} wasn't claimed (taken, cancelled or blocked meanwhile) - skipping.");
            return false;
        }

        Log.Info($"{run.IssueKey}: claimed run {run.Id}.");
        RunsInProgress.Save(runFilePath, new RunInProgress(run.Id, run.IssueKey, Workspace: null));
        Workspace workspace;
        try
        {
            var statuses = await FindEpicStatuses(space.Key, run.IssueKey, ct);
            var inProgress = statuses.Statuses.First(x => x.CategoryId == StatusCategories.InProgress);

            workspace = config.InCheckouts
                ? WorkspaceManager.CreateInCheckouts(space, run.IssueKey, run.IssueTitle)
                : WorkspaceManager.Create(space, config.WorktreeFolder, run.IssueKey, run.IssueTitle);
            if (workspace.CommitsSoFar > 0)
            {
                // Read from the file, not the config the runner started with: `lba review-comments` changes it while the
                // service runs.
                workspace = workspace with { Reviews = PullRequestReviews.Find(workspace, RunnerConfig.TryLoad(configPath) ?? config) };
            }

            Log.Info(workspace.InCheckouts
                ? $"{run.IssueKey}: {string.Join(", ", workspace.Repos.Select(x => x.Path))} switched to branch {workspace.Branch}."
                : workspace.IsMultiRepo
                    ? $"{run.IssueKey}: workspace {workspace.Path} with {string.Join(", ", workspace.Repos.Select(x => x.Name))} on branch {workspace.Branch}."
                    : $"{run.IssueKey}: worktree {workspace.Path} on branch {workspace.Branch}.");
            RunsInProgress.Save(runFilePath, new RunInProgress(run.Id, run.IssueKey, RunInProgressWorkspace.Of(workspace)));

            var where = workspace.IsMultiRepo || workspace.InCheckouts
                ? $"branch `{workspace.Branch}` in {string.Join(", ", workspace.Repos.Select(x => x.Name))}"
                : $"branch `{workspace.Branch}`";
            if (workspace.CommitsSoFar > 0)
            {
                where = $"continuing {where} ({workspace.CommitsSoFar} commit{(workspace.CommitsSoFar == 1 ? "" : "s")} so far)";
            }

            var reviewComments = workspace.Reviews.Sum(x => x.Comments.Count);
            if (reviewComments > 0)
            {
                where += $", with {reviewComments} open review comment{(reviewComments == 1 ? "" : "s")} from {string.Join(", ", workspace.Reviews.Where(x => x.Comments.Count > 0).Select(x => $"{x.Url} ({x.Level})"))}";
            }

            var skipped = workspace.Reviews.Sum(x => x.Skipped);
            if (skipped > 0)
            {
                where += $" ({skipped} comment{(skipped == 1 ? "" : "s")} by people this computer doesn't trust left out - see `lba review-comments`)";
            }
            await boards.EditIssueStatus(
                run.IssueKey,
                inProgress.Id,
                $"laraue-boards-agent picked this up on {Environment.MachineName}, {where}.",
                ct);
            Log.Info($"{run.IssueKey}: moved to {inProgress.Name} ({statuses.EpicName}).");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Log.Error($"{run.IssueKey}: {e.Message}");
            await Fail(run, "Couldn't start the work", e.Message);
            RunsInProgress.Clear(runFilePath);
            return true;
        }

        // Not in a finally: when Work throws or the runner is stopped, the record stays, so RecoverInterruptedRun picks
        // the run up when the runner starts again.
        if (!await Work(run, workspace, resumeSessionId: null, ct))
        {
            RunsInProgress.Clear(runFilePath);
        }

        return true;
    }

    /// <summary>A run is resumed after the runner stopped at most this many times, then failed.</summary>
    private const int MaxResumes = 3;

    /// <summary>
    /// Picks up the run this machine was working on when the runner stopped - an update or restart of the service, a
    /// reboot, a crash: while Boards still has it Working, the agent's session is resumed in the same workspace, on the
    /// same branch with its changes, so the work goes on instead of being thrown away. Without a workspace, or after
    /// <see cref="MaxResumes"/> resumes, the run is failed with what's on its branches, so the issue isn't left locked.
    /// A run that ended meanwhile is just forgotten.
    /// </summary>
    public async Task RecoverInterruptedRun(CancellationToken ct)
    {
        if (RunsInProgress.Load(runFilePath) is not { } saved)
        {
            return;
        }

        var issue = await boards.GetIssue(saved.IssueKey, ct);
        if (issue.LatestAgentRun is not { } run || run.Id != saved.RunId || run.State != AgentRunStates.Working)
        {
            RunsInProgress.Clear(runFilePath);
            return;
        }

        var workspace = saved.Workspace?.ToWorkspace(saved.IssueKey);
        if (workspace is not null && saved.Resumes < MaxResumes && (!workspace.InCheckouts || ContinueCommand.Prepare(workspace)))
        {
            Log.Info($"{saved.IssueKey}: run {saved.RunId} was interrupted when the runner stopped - resuming it (attempt {saved.Resumes + 1}).");
            RunsInProgress.Save(runFilePath, saved with { Resumes = saved.Resumes + 1 });
            if (!await Work(run, workspace, saved.SessionId, ct))
            {
                RunsInProgress.Clear(runFilePath);
            }

            return;
        }

        Log.Info($"{saved.IssueKey}: run {saved.RunId} was still Working when the runner stopped and can't be resumed - failing it.");
        var branches = "";
        if (workspace is not null)
        {
            var changed = WorkspaceManager.Finish(workspace);
            branches = changed.Count == 0
                ? "\n\nNo repository got commits."
                : "\n\nBranches with commits:\n" + string.Join("\n", changed.Select(x => $"- {x}"));
        }

        await Fail(
            run,
            "The runner stopped during the work",
            $"laraue-boards-agent on {Environment.MachineName} stopped while the agent was working and couldn't resume it, so the work was cut off. What the agent did so far stays on its branch." + branches);
        RunsInProgress.Clear(runFilePath);
    }

    /// <summary>
    /// Runs the coding agent in the worktree until the run ends: the agent finishes it, it's cancelled, or the timeout
    /// passes. A round that stops at the turn limit is continued by resuming the session (up to
    /// <see cref="RunnerConfig.MaxContinuations"/> times). <paramref name="resumeSessionId"/>: resume that session
    /// instead of starting the agent - the runner was restarted mid-run. Returns true when the runner itself was stopped:
    /// the run stays Working and its record is kept, so the next start resumes it.
    /// </summary>
    private async Task<bool> Work(AgentRunSummary run, Workspace workspace, string? resumeSessionId, CancellationToken ct)
    {
        var logPath = Path.Combine(config.WorktreeFolder, $"{run.IssueKey}.run-{run.Id}.jsonl");
        var sessionId = resumeSessionId;
        var agent = resumeSessionId is null
            ? codingAgent.Start(apiKey, run, workspace, logPath)
            : codingAgent.Resume(apiKey, run.IssueKey, workspace, resumeSessionId, ResumePrompt(run, "laraue-boards-agent was restarted while you were working, so you were interrupted."), logPath);
        try
        {
            // The session goes into the run record as soon as the agent says it, so a restart - or a crash - can resume it.
            void RecordSession()
            {
                if (agent.SessionId is { } id && id != sessionId)
                {
                    sessionId = id;
                    if (RunsInProgress.Load(runFilePath) is { } record && record.RunId == run.Id)
                    {
                        RunsInProgress.Save(runFilePath, record with { SessionId = id });
                    }
                }
            }

            void SaveSession()
            {
                RecordSession();
                if (sessionId is not null)
                {
                    AgentSessions.Save(
                        runFilePath,
                        new AgentSession(run.IssueKey, run.IssueTitle, run.IssueUrl, codingAgent.Key, sessionId, RunInProgressWorkspace.Of(workspace)));
                }
            }

            // However the run ends: what the agent didn't commit to is put back, and the runner's comment says which
            // branches have commits - right even if the agent's own summary misses one.
            string? branches = null;
            string Branches()
            {
                if (branches is null)
                {
                    // Every ending passes here once: the session is kept for `lba continue`.
                    SaveSession();
                    var changed = WorkspaceManager.Finish(workspace);
                    branches = !workspace.IsMultiRepo && !workspace.InCheckouts
                        ? ""
                        : changed.Count == 0
                            ? "\n\nNo repository got commits."
                            : "\n\nBranches with commits:\n" + string.Join("\n", changed.Select(x => $"- {x}"));
                }

                return branches;
            }

            Log.Info($"{run.IssueKey}: {codingAgent.Name} {(resumeSessionId is null ? "started" : "resumed")} (pid {agent.ProcessId}), log {logPath}.");
            if (resumeSessionId is null)
            {
                Notifications.Show(config, $"{run.IssueKey} · agent started", run.IssueTitle, run.IssueUrl);
            }

            var deadline = DateTime.UtcNow.AddMinutes(config.TimeoutMinutes);
            var continuations = 0;
            while (true)
            {
                Task tick = Task.Delay(TimeSpan.FromSeconds(config.CancelCheckSeconds), CancellationToken.None);
                Task exited = agent.WaitForExitAsync();
                var stopped = Task.Delay(Timeout.Infinite, ct);
                var first = await Task.WhenAny(exited, tick, stopped);
                RecordSession();

                if (first == stopped)
                {
                    // The service is being restarted or the computer shut down: keep the run, resume it on the next start.
                    agent.Kill();
                    SaveSession();
                    Log.Info($"{run.IssueKey}: the runner was stopped - {codingAgent.Name} stopped; run {run.Id} stays Working and resumes when the runner starts again.");
                    return true;
                }

                if (first == exited)
                {
                    var finalState = await GetRunState(run, CancellationToken.None);
                    Log.Info($"{run.IssueKey}: {codingAgent.Name} exited ({agent.ExitCode}), run {run.Id} is {finalState}. {agent.Summary()}");
                    if (finalState == AgentRunStates.Working
                        && agent.HitTurnLimit
                        && continuations < config.MaxContinuations
                        && DateTime.UtcNow < deadline
                        && sessionId is not null)
                    {
                        continuations++;
                        Log.Info($"{run.IssueKey}: out of turns - continuing the session (round {continuations + 1}).");
                        agent.Dispose();
                        agent = codingAgent.Resume(apiKey, run.IssueKey, workspace, sessionId, ResumePrompt(run, "You ran out of turns before finishing."), logPath);
                        continue;
                    }

                    await Finished(run, workspace, agent, finalState, Branches);
                    return false;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    agent.Kill();
                    Log.Info($"{run.IssueKey}: timed out after {config.TimeoutMinutes} min - {codingAgent.Name} killed.");
                    await Fail(run, $"Timed out after {config.TimeoutMinutes} min", agent.Summary() + Branches());
                    return false;
                }

                string? state;
                try
                {
                    state = await GetRunState(run, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    // A dropped connection mustn't end the run: the agent keeps working, the next check tries again.
                    Log.Error($"{run.IssueKey}: couldn't check the run ({e.GetType().Name}: {e.Message}) - checking again in {config.CancelCheckSeconds} s.");
                    continue;
                }

                if (state == AgentRunStates.Cancelled)
                {
                    agent.Kill();
                    Log.Info($"{run.IssueKey}: run {run.Id} was cancelled - {codingAgent.Name} killed.");
                    Branches();
                    Notifications.Show(config, $"{run.IssueKey} · agent run cancelled", run.IssueTitle, run.IssueUrl);
                    return false;
                }
            }
        }
        finally
        {
            agent.Dispose();
        }
    }

    /// <summary>The prompt that resumes the run's session: why it stopped, and to carry on with the same task.</summary>
    private static string ResumePrompt(AgentRunSummary run, string why)
    {
        return $"""
            {why} Continue working on {run.IssueKey} (agent run id: {run.Id}) from where you stopped - your changes so far are on
            the branch, committed or not. Finish the task as the work-on-issue skill says: commit, post the summary, move the
            issue to review and finish the agent run.
            """;
    }

    /// <summary>The agent exited and the run won't be continued: report it as it ended.</summary>
    private async Task Finished(AgentRunSummary run, Workspace workspace, AgentProcess agent, string? finalState, Func<string> branches)
    {
        if (finalState == AgentRunStates.Working)
        {
            await Fail(run, $"{codingAgent.Name} stopped without finishing", $"Exit code {agent.ExitCode}. {agent.Summary()}" + branches());
        }
        else if (finalState is AgentRunStates.Done or AgentRunStates.Failed)
        {
            var branchesText = branches();
            var published = finalState == AgentRunStates.Done ? Publisher.Publish(config, run, workspace) : [];
            var publishedText = published.Count == 0 ? "" : "\n\nPublished:\n" + string.Join("\n", published.Select(x => $"- {x.Line}"));

            // The agent can't know what it cost; the runner adds it under the agent's own summary.
            await boards.CreateComment(run.IssueKey, $"_laraue-boards-agent: {agent.Summary()}_" + branchesText + publishedText, CancellationToken.None);
            if (finalState == AgentRunStates.Done)
            {
                var pullRequest = published.Select(x => x.PullRequestUrl).OfType<string>().FirstOrDefault();
                Notifications.Show(
                    config,
                    $"{run.IssueKey} · ready for review",
                    pullRequest is null ? run.IssueTitle : $"{run.IssueTitle} - {pullRequest}",
                    run.IssueUrl);
            }
            else
            {
                Notifications.Show(config, $"{run.IssueKey} · agent failed", run.FailureReason ?? run.IssueTitle, run.IssueUrl);
            }
        }
        else
        {
            branches();
        }
    }

    /// <summary>The run's state now, or null when it isn't the issue's latest run anymore.</summary>
    private async Task<string?> GetRunState(AgentRunSummary run, CancellationToken ct)
    {
        var issue = await boards.GetIssue(run.IssueKey, ct);
        return issue.LatestAgentRun?.Id == run.Id ? issue.LatestAgentRun.State : null;
    }

    /// <summary>The statuses of the issue's epic, in board order.</summary>
    private async Task<EpicStatusSummary> FindEpicStatuses(string spaceKey, string issueKey, CancellationToken ct)
    {
        var issue = await boards.GetIssue(issueKey, ct);
        var epics = await boards.ListStatuses(spaceKey, ct);
        return epics.FirstOrDefault(x => x.EpicId == issue.EpicId)
            ?? throw new InvalidOperationException($"the statuses of {issueKey}'s epic weren't found in {spaceKey}");
    }

    /// <summary>
    /// Fails the run: the short reason goes on the run (the web app shows it on the agent badge), the details in a
    /// comment. Not cancellable: a run left Working would keep the issue locked.
    /// </summary>
    private async Task Fail(AgentRunSummary run, string reason, string details)
    {
        var comment = $"""
            **Agent run failed: {reason}.**

            {details}

            Move the issue back to a not-started status to hand it to the agent again.
            """;
        if (comment.Length > MaxReasonLength)
        {
            comment = comment[..MaxReasonLength];
        }

        Notifications.Show(config, $"{run.IssueKey} · agent failed", reason, run.IssueUrl);
        try
        {
            await boards.CreateComment(run.IssueKey, comment, CancellationToken.None);
            if (await boards.FinishAgentRun(run.Id, AgentRunStates.Failed, reason, CancellationToken.None))
            {
                Log.Info($"{run.IssueKey}: run {run.Id} finished as Failed.");
            }
            else
            {
                Log.Info($"{run.IssueKey}: run {run.Id} was cancelled meanwhile.");
            }
        }
        catch (Exception e)
        {
            Log.Error($"{run.IssueKey}: couldn't report the failure: {e.Message}");
        }
    }
}
