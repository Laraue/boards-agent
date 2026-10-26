namespace Laraue.Boards.Agent.Boards;

/// <summary>The Boards MCP tools the runner calls, as the user its API key belongs to.</summary>
public interface IBoardsClient : IAsyncDisposable
{
    Task<MeInfo> GetMe(CancellationToken ct);

    Task<IReadOnlyList<SpaceSummary>> ListSpaces(CancellationToken ct);

    /// <summary>A space's description and links (Boards' get_space).</summary>
    Task<SpaceDetail> GetSpace(string spaceKey, CancellationToken ct);

    /// <summary>The user's own Ready runs that aren't waiting for a blocker - in one space, or every space when null.</summary>
    Task<IReadOnlyList<AgentRunSummary>> ListReadyRuns(string? spaceKey, Guid userId, CancellationToken ct);

    /// <summary>False when the run was taken, cancelled or is blocked. Throws (403) when the issue isn't the user's.</summary>
    Task<bool> ClaimAgentRun(long runId, CancellationToken ct);

    /// <summary>
    /// A new run of the issue that is already Working, claimed by this key - for `lba continue`. Throws (400) when the
    /// issue already has an open run, (403) when the issue isn't the user's.
    /// </summary>
    Task<AgentRunSummary> StartAgentRun(string issueKey, CancellationToken ct);

    /// <summary>False when the run had already ended.</summary>
    Task<bool> CancelAgentRun(long runId, CancellationToken ct);

    /// <summary>False when the run isn't Working anymore (cancelled meanwhile).</summary>
    Task<bool> FinishAgentRun(long runId, string state, string? reason, CancellationToken ct);

    Task<IReadOnlyList<EpicStatusSummary>> ListStatuses(string spaceKey, CancellationToken ct);

    Task<IssueDetail> GetIssue(string issueKey, CancellationToken ct);

    Task EditIssueStatus(string issueKey, long statusId, string? comment, CancellationToken ct);

    Task CreateComment(string issueKey, string text, CancellationToken ct);

    /// <summary>The issue's comments after the first <paramref name="skip"/>, oldest first.</summary>
    Task<IReadOnlyList<IssueComment>> ListIssueCommentsFrom(string issueKey, int skip, CancellationToken ct);
}
