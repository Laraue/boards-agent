using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Publishing;

namespace Laraue.Boards.Agent.Tests.Publishing;

public class ReviewRepliesTests
{
    private const string Signature = "\n\n_laraue-boards-agent, abc1234_";

    /// <summary>R1 and R2 in one thread, R3 alone in another, R4 a review summary, R5 a conversation comment.</summary>
    private static readonly PullRequestReview Review = new PullRequestReview(
        "https://github.com/acme/api/pull/3",
        [
            new PullRequestReviewComment("src/Api.cs:15", "@owner", "Make it a service.", "T1"),
            new PullRequestReviewComment("src/Api.cs:15", "@member", "Agreed.", "T1"),
            new PullRequestReviewComment("src/Api.cs:23", "@owner", "Lock epics first.", "T2"),
            new PullRequestReviewComment("review", "@member", "Overall fine"),
            new PullRequestReviewComment("comment", "@owner", "Also update the docs"),
        ],
        ReviewCommentLevels.Collaborators,
        Skipped: 0) { RepoPath = "/repos/api" };

    [Fact]
    public void ParseOutcomes_ShouldReadOutcomeLinesInAnyCommonForm_WhenTheMessageEndsWithThem()
    {
        var outcomes = ReviewReplies.ParseOutcomes("""
            Done: moved the recalculation and locked the epics.

            R1: fixed - moved it into EpicStatusService
            - r2 - not changed - it was agreement, nothing to do
            [R3] fixed: epics are locked top-down
            `R4: fixed - docs updated`
            R2D2 isn't a reference
            """);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["R1"] = "fixed - moved it into EpicStatusService",
                ["R2"] = "not changed - it was agreement, nothing to do",
                ["R3"] = "fixed: epics are locked top-down",
                ["R4"] = "fixed - docs updated",
            },
            outcomes);
    }

    [Fact]
    public void Plan_ShouldReplyInEachThreadAndSummarizeTheRest_WhenTheAgentGaveOutcomes()
    {
        var replies = ReviewReplies.Plan(
            [Review],
            "R1: fixed - moved it\nR2: not changed - just agreement\nR3: fixed - locked\nR4: fixed - thanks\nR5: fixed - docs updated",
            resolveFixed: false,
            "abc1234");

        Assert.Equal(
            [
                new ReviewReply("/repos/api", Review.Url, "T1", "- @owner: fixed - moved it\n- @member: not changed - just agreement" + Signature, Resolve: false),
                new ReviewReply("/repos/api", Review.Url, "T2", "fixed - locked" + Signature, Resolve: false),
                new ReviewReply(
                    "/repos/api",
                    Review.Url,
                    ThreadId: null,
                    "On the review:\n- @member's review: fixed - thanks\n- @owner's comment: fixed - docs updated" + Signature,
                    Resolve: false),
            ],
            replies);
    }

    [Fact]
    public void Plan_ShouldResolveOnlyThreadsWhoseEveryCommentWasFixed_WhenResolvingIsOn()
    {
        var replies = ReviewReplies.Plan([Review], "R1: fixed - moved it\nR2: not changed - agreement\nR3: fixed - locked", resolveFixed: true, "abc1234");

        Assert.Equal([false, true], replies.Select(x => x.Resolve));
    }

    [Fact]
    public void Plan_ShouldReplyToNothing_WhenTheAgentGaveNoOutcomes()
    {
        Assert.Empty(ReviewReplies.Plan([Review], "Done, see the commits.", resolveFixed: true, "abc1234"));
    }

    [Fact]
    public void Plan_ShouldSkipTheReview_WhenItsRepositoryIsUnknown()
    {
        Assert.Empty(ReviewReplies.Plan([Review with { RepoPath = null }], "R3: fixed - locked", resolveFixed: false, "abc1234"));
    }
}
