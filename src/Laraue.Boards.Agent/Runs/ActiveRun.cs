using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Runs;

/// <summary>
/// The agent working on one run, with what the runner keeps track of meanwhile: the agent's session - recorded as soon as
/// the agent says it, so a restart or a crash can resume it - and, once the run ends, which branches got commits.
/// </summary>
/// <param name="runFilePath">Where the run in progress is recorded (<see cref="RunsInProgress"/>).</param>
sealed class ActiveRun(
    AgentRunSummary run,
    Workspace workspace,
    string runFilePath,
    string agentKey,
    AgentProcess agent,
    string? sessionId) : IDisposable
{
    private string? _branches;

    public AgentRunSummary Run => run;

    public Workspace Workspace => workspace;

    public AgentProcess Agent { get; private set; } = agent;

    public string? SessionId { get; private set; } = sessionId;

    /// <summary>The agent's next process - its session resumed - in place of the one that exited.</summary>
    public void Continue(AgentProcess next)
    {
        Agent.Dispose();
        Agent = next;
    }

    /// <summary>Puts the agent's session into the run record once the agent said it.</summary>
    public void RecordSession()
    {
        if (Agent.SessionId is { } id && id != SessionId)
        {
            SessionId = id;
            if (RunsInProgress.Load(runFilePath) is { } record && record.RunId == run.Id)
            {
                RunsInProgress.Save(runFilePath, record with { SessionId = id });
            }
        }
    }

    /// <summary>Keeps the session for `lba continue`.</summary>
    public void SaveSession()
    {
        RecordSession();
        if (SessionId is not null)
        {
            AgentSessions.Save(
                runFilePath,
                new AgentSession(run.IssueKey, run.IssueTitle, run.IssueUrl, agentKey, SessionId, RunInProgressWorkspace.Of(workspace)));
        }
    }

    /// <summary>
    /// However the run ends - once: the session is saved, what the agent didn't commit to is put back, and the text for the
    /// runner's comment says which branches have commits, right even if the agent's own summary misses one. Empty for a
    /// single worktree, whose branch the comment needn't name.
    /// </summary>
    public string Branches()
    {
        if (_branches is null)
        {
            SaveSession();
            var changed = WorkspaceManager.Finish(workspace);
            _branches = !workspace.IsMultiRepo && !workspace.InCheckouts ? "" : BranchesText(changed);
        }

        return _branches;
    }

    /// <summary>The branches with commits as a comment's paragraph, e.g. "Branches with commits: - web-app: ...".</summary>
    public static string BranchesText(IReadOnlyList<string> changed)
    {
        return changed.Count == 0
            ? "\n\nNo repository got commits."
            : "\n\nBranches with commits:\n" + string.Join("\n", changed.Select(x => $"- {x}"));
    }

    public void Dispose()
    {
        Agent.Dispose();
    }
}
