using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Tests.TestSupport;

namespace Laraue.Boards.Agent.Tests.Workspaces;

public class GitCommitsSinceTests : IDisposable
{
    private readonly TestGit _git = new();

    [Fact]
    public void CommitsSince_ShouldCountTheBranchNotHead_WhenAnotherBranchIsCheckedOut()
    {
        var baseCommit = _git.Head(_git.Clone);
        _git.Run(_git.Clone, "switch", "-c", "agent/BRD-1-x");
        _git.Commit(_git.Clone, "a.txt", "a");
        _git.Run(_git.Clone, "switch", "-c", "mine", baseCommit);
        _git.Commit(_git.Clone, "b.txt", "b");
        _git.Commit(_git.Clone, "c.txt", "c");

        Assert.Equal(1, Git.CommitsSince(_git.Clone, baseCommit, "agent/BRD-1-x"));
    }

    [Fact]
    public void CommitsSince_ShouldBeMinusOne_WhenTheBranchDoesntExist()
    {
        Assert.Equal(-1, Git.CommitsSince(_git.Clone, _git.Head(_git.Clone), "agent/BRD-1-deleted"));
    }

    [Fact]
    public void PushedCommit_ShouldBeTheBranchesCommit_WhenOriginHasIt()
    {
        _git.Run(_git.Clone, "switch", "-c", "agent/BRD-1-x");
        _git.Commit(_git.Clone, "a.txt", "a");
        _git.Run(_git.Clone, "push", "origin", "agent/BRD-1-x");

        Assert.Equal(_git.Head(_git.Clone), Git.PushedCommit(_git.Clone, "agent/BRD-1-x"));
    }

    [Fact]
    public void PushedCommit_ShouldBeNull_WhenTheBranchHasCommitsOriginDoesnt()
    {
        _git.Run(_git.Clone, "switch", "-c", "agent/BRD-1-x");
        _git.Commit(_git.Clone, "a.txt", "a");
        _git.Run(_git.Clone, "push", "origin", "agent/BRD-1-x");
        _git.Commit(_git.Clone, "b.txt", "b");

        Assert.Null(Git.PushedCommit(_git.Clone, "agent/BRD-1-x"));
    }

    public void Dispose()
    {
        _git.Dispose();
    }
}
