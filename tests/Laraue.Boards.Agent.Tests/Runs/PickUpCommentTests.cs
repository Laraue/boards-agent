using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Publishing;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Tests.TestSupport;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Tests.Runs;

public class PickUpCommentTests
{
    [Fact]
    public void PickUpComment_ShouldNameTheMachineAndBranch_WhenTheRunIsNew()
    {
        var comment = Runner.PickUpComment(TestData.Workspace(TestData.Repo()), "dev-laptop");

        Assert.Equal("laraue-boards-agent picked this up on dev-laptop, branch `agent/BRD-42-sort-epics`.", comment);
    }

    [Fact]
    public void PickUpComment_ShouldNameTheRepositories_WhenTheWorkspaceHasSeveral()
    {
        var workspace = TestData.Workspace(TestData.Repo("api"), TestData.Repo("web"));

        var comment = Runner.PickUpComment(workspace, "dev-laptop");

        Assert.Equal("laraue-boards-agent picked this up on dev-laptop, branch `agent/BRD-42-sort-epics` in api, web.", comment);
    }

    [Fact]
    public void PickUpComment_ShouldSayItStartedANewBranch_WhenTheEarlierOneWasMerged()
    {
        var workspace = TestData.Workspace(TestData.Repo()) with { MergedBranch = "agent/BRD-42-old" };

        var comment = Runner.PickUpComment(workspace, "dev-laptop");

        Assert.Equal(
            "laraue-boards-agent picked this up on dev-laptop, branch `agent/BRD-42-sort-epics`, new from origin/main since `agent/BRD-42-old` was merged.",
            comment);
    }

    [Fact]
    public void PickUpComment_ShouldSayWhatItContinuesAndWhichReviewsItGot_WhenTheBranchHasEarlierWork()
    {
        var review = new PullRequestReview(
            "https://github.com/acme/web-app/pull/3",
            [new PullRequestReviewComment("a.cs:1", "@owner", "Rename")],
            ReviewCommentLevels.Collaborators,
            Skipped: 2);
        var workspace = TestData.Workspace(TestData.Repo()) with { CommitsSoFar = 1, Reviews = [review] };

        var comment = Runner.PickUpComment(workspace, "dev-laptop");

        Assert.Equal(
            "laraue-boards-agent picked this up on dev-laptop, continuing branch `agent/BRD-42-sort-epics` (1 commit so far), "
                + "with 1 open review comment from https://github.com/acme/web-app/pull/3 (collaborators) "
                + "(2 comments by people this computer doesn't trust left out - see `lba review-comments`).",
            comment);
    }
}
