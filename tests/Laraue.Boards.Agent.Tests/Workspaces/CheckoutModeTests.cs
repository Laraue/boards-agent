using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Tests.TestSupport;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Tests.Workspaces;

/// <summary>Checkout mode (BRD-313): the agent works in the person's own clone, switched to the issue's branch.</summary>
public class CheckoutModeTests : IDisposable
{
    private const string Branch = "agent/BRD-42-sort-epics";

    private readonly TestGit _git = new();

    private SpaceContext Space => TestData.Space(new SpaceRepo("clone", _git.Clone, "origin/main", "https://github.com/acme/clone", null));

    [Fact]
    public void CreateInCheckouts_ShouldSwitchTheCloneToANewBranchFromOrigin_WhenTheIssueIsNew()
    {
        var workspace = WorkspaceManager.CreateInCheckouts(Space, "BRD-42", "Sort epics");

        Assert.Equal(Branch, workspace.Branch);
        Assert.Equal(Branch, Git.CurrentBranch(_git.Clone));
        Assert.Equal("main", workspace.Repos[0].PreviousBranch);
        Assert.Equal(_git.Head(_git.Clone, "origin/main"), workspace.Repos[0].BaseCommit);
        Assert.Equal(0, workspace.CommitsSoFar);
    }

    [Fact]
    public void Finish_ShouldSwitchBackAndDeleteTheBranch_WhenTheAgentCommittedNothing()
    {
        var workspace = WorkspaceManager.CreateInCheckouts(Space, "BRD-42", "Sort epics");

        var changed = WorkspaceManager.Finish(workspace);

        Assert.Empty(changed);
        Assert.Equal("main", Git.CurrentBranch(_git.Clone));
        Assert.False(_git.BranchExists(_git.Clone, Branch));
    }

    [Fact]
    public void Finish_ShouldKeepTheBranchCheckedOut_WhenTheAgentCommitted()
    {
        var workspace = WorkspaceManager.CreateInCheckouts(Space, "BRD-42", "Sort epics");
        _git.Commit(_git.Clone, "fix.txt", "fixed");

        var changed = WorkspaceManager.Finish(workspace);

        Assert.Equal([$"clone: `{Branch}`, 1 commit"], changed);
        Assert.Equal(Branch, Git.CurrentBranch(_git.Clone));
    }

    [Fact]
    public void CreateInCheckouts_ShouldContinueTheIssuesBranch_WhenAnEarlierRunLeftCommitsAndTheIssueWasRenamed()
    {
        WorkspaceManager.CreateInCheckouts(Space, "BRD-42", "Sort epics");
        _git.Commit(_git.Clone, "fix.txt", "fixed");
        _git.Run(_git.Clone, "switch", "main");

        var workspace = WorkspaceManager.CreateInCheckouts(Space, "BRD-42", "Sort epics in the space page");

        Assert.Equal(Branch, workspace.Branch);
        Assert.Equal(1, workspace.CommitsSoFar);
    }

    [Fact]
    public void CreateInCheckouts_ShouldThrowAndLeaveTheClone_WhenItHasUncommittedChanges()
    {
        File.WriteAllText(Path.Combine(_git.Clone, "draft.txt"), "the person's work");

        Assert.Throws<InvalidOperationException>(() => WorkspaceManager.CreateInCheckouts(Space, "BRD-42", "Sort epics"));
        Assert.Equal("main", Git.CurrentBranch(_git.Clone));
    }

    public void Dispose()
    {
        _git.Dispose();
    }
}
