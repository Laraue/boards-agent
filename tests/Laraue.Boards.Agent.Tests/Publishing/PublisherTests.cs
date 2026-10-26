using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Publishing;
using Laraue.Boards.Agent.Tests.TestSupport;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Tests.Publishing;

public class PublisherTests : IDisposable
{
    private const string Branch = "agent/BRD-42-sort-epics";

    private readonly TestGit _git = new();
    private readonly RunnerConfig _push = new() { Publish = PublishModes.Push, Notifications = false };

    [Fact]
    public void Publish_ShouldPushTheBranch_WhenItHasCommits()
    {
        var workspace = Workspace(_git.Head(_git.Clone));
        _git.Run(_git.Clone, "switch", "-c", Branch);
        _git.Commit(_git.Clone, "fix.txt", "fixed");

        var published = Publisher.Publish(_push, TestData.Run(), workspace);

        Assert.Equal([new PublishedRepo($"clone: pushed `{Branch}`", null)], published);
        Assert.True(_git.BranchExists(_git.Origin, Branch));
    }

    [Fact]
    public void Publish_ShouldSkipTheRepository_WhenItsEmptyBranchWasDeletedAndTheCloneIsOnAnotherBranch()
    {
        // Checkout mode: Finish switched the clone back to the person's branch, which has commits of its own.
        var workspace = Workspace(_git.Head(_git.Clone));
        _git.Run(_git.Clone, "switch", "-c", "my-work");
        _git.Commit(_git.Clone, "mine.txt", "mine");

        var published = Publisher.Publish(_push, TestData.Run(), workspace);

        Assert.Empty(published);
        Assert.False(_git.BranchExists(_git.Origin, Branch));
    }

    [Fact]
    public void Publish_ShouldDoNothing_WhenPublishingIsOff()
    {
        var workspace = Workspace(_git.Head(_git.Clone));
        _git.Run(_git.Clone, "switch", "-c", Branch);
        _git.Commit(_git.Clone, "fix.txt", "fixed");

        var published = Publisher.Publish(new RunnerConfig { Publish = PublishModes.Off }, TestData.Run(), workspace);

        Assert.Empty(published);
        Assert.False(_git.BranchExists(_git.Origin, Branch));
    }

    public void Dispose()
    {
        _git.Dispose();
    }

    private Workspace Workspace(string baseCommit)
    {
        var repo = new WorkspaceRepo("clone", _git.Clone, _git.Clone, "origin/main", baseCommit, "https://github.com/acme/clone", null);
        return new Workspace(_git.Clone, Branch, [repo], TestData.Space(), InCheckouts: true);
    }
}
