namespace Laraue.Boards.Agent.Boards;

public sealed record MeInfo(MeUser User, MeOrganization Organization);

public sealed record MeUser(Guid Id, string DisplayName);

public sealed record MeOrganization(long Id, string Name, bool IsPersonal);

public sealed record AgentRunListPage(IReadOnlyList<AgentRunSummary> Runs, long Page, bool HasNextPage);

public sealed record AgentRunSummary(
    long Id,
    string IssueKey,
    string IssueTitle,
    string IssueUrl,
    string State,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime? ClaimedAt,
    string? ClaimedByApiKey,
    DateTime? FinishedAt,
    string? FailureReason,
    bool IsBlocked);

public sealed record StatusSummary(long Id, string Name, int CategoryId, string Category);

public sealed record SpaceSummary(string Key, string Name, bool CanCreateIssue);

public sealed record SpaceDetail(string Key, string Name, string? Description, IReadOnlyList<SpaceLinkSummary> Links);

public sealed record SpaceLinkSummary(string Url, string Description);

public sealed record EpicStatusSummary(long EpicId, string EpicName, IReadOnlyList<StatusSummary> Statuses);

public sealed record IssueDetail(string Key, string Title, string Status, long? StatusId, long? EpicId, int StatusCategoryId, int CommentCount, AgentRunSummary? LatestAgentRun);

public sealed record IssueCommentPage(IReadOnlyList<IssueComment> Comments, long Page, bool HasNextPage);

/// <summary>A comment, as far as the runner reads it: whose it is and its attachments.</summary>
public sealed record IssueComment(long Id, string? ApiKeyName, bool CanManage, IReadOnlyList<CommentAttachment> Attachments);

public sealed record CommentAttachment(Guid Id, string? FileName);
