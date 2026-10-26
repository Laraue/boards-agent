using Laraue.Boards.Agent.Commands;

namespace Laraue.Boards.Agent.Tests.Commands;

public class RunnerArgsTests
{
    [Fact]
    public void Parse_ShouldTakeTheIssueKeyAndUnquotedInstruction_WhenContinueGetsBoth()
    {
        var options = RunnerArgs.Parse(["continue", "min-21", "print", "the", "json", "--config", "/tmp/c.json"]);

        Assert.Equal(RunnerArgs.ContinueCommand, options.Command);
        Assert.Equal("MIN-21", options.IssueKey);
        Assert.Equal("print the json", options.Instruction);
        Assert.Equal("/tmp/c.json", options.ConfigPath);
    }

    [Fact]
    public void Parse_ShouldLeaveTheIssueKeyEmpty_WhenContinueGetsOnlyAnInstruction()
    {
        var options = RunnerArgs.Parse(["continue", "fix", "the", "tests"]);

        Assert.Null(options.IssueKey);
        Assert.Equal("fix the tests", options.Instruction);
    }

    [Fact]
    public void Parse_ShouldTakeNoInstruction_WhenContinueGetsNothing()
    {
        var options = RunnerArgs.Parse(["continue"]);

        Assert.Null(options.IssueKey);
        Assert.Null(options.Instruction);
    }

    [Fact]
    public void Parse_ShouldMeanServiceStatus_WhenTheCommandIsStatus()
    {
        var options = RunnerArgs.Parse(["status"]);

        Assert.Equal(RunnerArgs.ServiceCommand, options.Command);
        Assert.Equal("status", options.ServiceAction);
    }

    [Fact]
    public void Parse_ShouldTakeTheRepositoryAndLevel_WhenReviewCommentsGetsBoth()
    {
        var options = RunnerArgs.Parse(["review-comments", "acme/api", "owner"]);

        Assert.Equal(RunnerArgs.ReviewCommentsCommand, options.Command);
        Assert.Equal(["acme/api", "owner"], options.ReviewCommentsArguments);
        Assert.False(options.ReviewCommentsDefault);
    }

    [Fact]
    public void Parse_ShouldSetTheDefaultLevel_WhenReviewCommentsGetsDefault()
    {
        var options = RunnerArgs.Parse(["review-comments", "--default", "collaborators"]);

        Assert.True(options.ReviewCommentsDefault);
        Assert.Equal(["collaborators"], options.ReviewCommentsArguments);
    }

    [Fact]
    public void Parse_ShouldThrow_WhenReviewCommentsGetsTooManyArguments()
    {
        Assert.Throws<ArgumentException>(() => RunnerArgs.Parse(["review-comments", "acme/api", "owner", "extra"]));
    }

    [Fact]
    public void Parse_ShouldThrowWithTheUsage_WhenAnArgumentIsUnknown()
    {
        var error = Assert.Throws<ArgumentException>(() => RunnerArgs.Parse(["--bogus"]));

        Assert.Contains("Unknown argument: --bogus", error.Message);
        Assert.Contains("Usage:", error.Message);
    }

    [Fact]
    public void Parse_ShouldThrow_WhenTheServiceActionIsUnknown()
    {
        Assert.Throws<ArgumentException>(() => RunnerArgs.Parse(["service", "reboot"]));
    }
}
