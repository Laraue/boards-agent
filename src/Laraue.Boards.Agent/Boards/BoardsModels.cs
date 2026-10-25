namespace Laraue.Boards.Agent.Boards;

sealed record MeInfo(MeUser User, MeOrganization Organization);

sealed record MeUser(Guid Id, string DisplayName);

sealed record MeOrganization(long Id, string Name, bool IsPersonal);

sealed record AgentRunListPage(IReadOnlyList<AgentRunSummary> Runs, long Page, bool HasNextPage);

sealed record AgentRunSummary(
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

sealed record StatusSummary(long Id, string Name, int CategoryId, string Category);

sealed record SpaceSummary(string Key, string Name, bool CanCreateIssue);

sealed record SpaceDetail(string Key, string Name, string? Description, IReadOnlyList<SpaceLinkSummary> Links);

sealed record SpaceLinkSummary(string Url, string Description);

sealed record EpicStatusSummary(long EpicId, string EpicName, IReadOnlyList<StatusSummary> Statuses);

sealed record IssueDetail(string Key, string Title, string Status, long? StatusId, long? EpicId, int StatusCategoryId, int CommentCount, AgentRunSummary? LatestAgentRun);

sealed record IssueCommentPage(IReadOnlyList<IssueComment> Comments, long Page, bool HasNextPage);

/// <summary>A comment, as far as the runner reads it: whose it is and its attachments.</summary>
sealed record IssueComment(long Id, string? ApiKeyName, bool CanManage, IReadOnlyList<CommentAttachment> Attachments);

sealed record CommentAttachment(Guid Id, string? FileName);
