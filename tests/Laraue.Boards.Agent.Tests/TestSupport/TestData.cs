using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Tests.TestSupport;

static class TestData
{
    public static AgentRunSummary Run(long id = 5, string issueKey = "BRD-42", string state = AgentRunStates.Ready)
    {
        return new AgentRunSummary(
            id,
            issueKey,
            "Sort epics",
            $"https://boards.example/issues/{issueKey}",
            state,
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            "Ilya",
            ClaimedAt: null,
            ClaimedByApiKey: null,
            FinishedAt: null,
            FailureReason: null,
            IsBlocked: false);
    }

    public static IssueDetail Issue(string issueKey = "BRD-42", long epicId = 7, AgentRunSummary? latestRun = null)
    {
        return new IssueDetail(issueKey, "Sort epics", "To Do", StatusId: 1, epicId, StatusCategories.Created, CommentCount: 0, latestRun);
    }

    public static SpaceContext Space(params SpaceRepo[] repos)
    {
        return new SpaceContext("BRD", "Boards", Description: null, repos, OtherLinks: []);
    }

    public static Workspace Workspace(params WorkspaceRepo[] repos)
    {
        return new Workspace(repos[0].Path, "agent/BRD-42-sort-epics", repos, Space(), InCheckouts: false);
    }

    public static WorkspaceRepo Repo(string name = "web-app", string? path = null)
    {
        var folder = path ?? $"/repos/{name}";
        return new WorkspaceRepo(name, folder, folder, "origin/main", BaseCommit: null, Url: $"https://github.com/acme/{name}", Description: null);
    }
}
