using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Tests.TestSupport;
using Moq;

namespace Laraue.Boards.Agent.Tests.Runs;

public class RunnerTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Mock<IBoardsClient> _boards = new();
    private readonly Runner _runner;
    private readonly string _runFile;

    public RunnerTests()
    {
        var configPath = _directory.Combine("config.json");
        _runFile = RunsInProgress.PathFor(configPath);
        var config = new RunnerConfig { Notifications = false, WorktreeFolder = _directory.Path };
        _runner = new Runner(_boards.Object, config, Mock.Of<ICodingAgent>(), "api-key", _runFile, configPath);
    }

    [Fact]
    public async Task TryProcess_ShouldTouchNothing_WhenTheRunWasntClaimed()
    {
        var run = TestData.Run();
        _boards.Setup(x => x.ClaimAgentRun(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var processed = await _runner.TryProcess(TestData.Space(), run, CancellationToken.None);

        Assert.False(processed);
        Assert.Null(RunsInProgress.Load(_runFile));
        _boards.Verify(x => x.ClaimAgentRun(run.Id, It.IsAny<CancellationToken>()));
        _boards.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TryProcess_ShouldFailTheRunWithAComment_WhenTheWorkCantStart()
    {
        var run = TestData.Run();
        _boards.Setup(x => x.ClaimAgentRun(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _boards.Setup(x => x.GetIssue(run.IssueKey, It.IsAny<CancellationToken>())).ReturnsAsync(TestData.Issue(epicId: 7));
        // The issue's epic isn't among the space's: there's no In Progress status to move it to.
        _boards.Setup(x => x.ListStatuses("BRD", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _boards.Setup(x => x.FinishAgentRun(run.Id, AgentRunStates.Failed, "Couldn't start the work", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var processed = await _runner.TryProcess(TestData.Space(), run, CancellationToken.None);

        Assert.True(processed);
        Assert.Null(RunsInProgress.Load(_runFile));
        _boards.Verify(x => x.CreateComment(
            run.IssueKey,
            It.Is<string>(text => text.Contains("**Agent run failed: Couldn't start the work.**") && text.Contains("weren't found in BRD")),
            It.IsAny<CancellationToken>()));
        _boards.Verify(x => x.FinishAgentRun(run.Id, AgentRunStates.Failed, "Couldn't start the work", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task RecoverInterruptedRun_ShouldForgetTheRecord_WhenTheRunEndedMeanwhile()
    {
        var run = TestData.Run(state: AgentRunStates.Done);
        RunsInProgress.Save(_runFile, new RunInProgress(run.Id, run.IssueKey, Workspace: null));
        _boards.Setup(x => x.GetIssue(run.IssueKey, It.IsAny<CancellationToken>())).ReturnsAsync(TestData.Issue(latestRun: run));

        await _runner.RecoverInterruptedRun(CancellationToken.None);

        Assert.Null(RunsInProgress.Load(_runFile));
        _boards.Verify(x => x.FinishAgentRun(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecoverInterruptedRun_ShouldFailTheRun_WhenItStoppedBeforeItHadAWorkspace()
    {
        var run = TestData.Run(state: AgentRunStates.Working);
        RunsInProgress.Save(_runFile, new RunInProgress(run.Id, run.IssueKey, Workspace: null));
        _boards.Setup(x => x.GetIssue(run.IssueKey, It.IsAny<CancellationToken>())).ReturnsAsync(TestData.Issue(latestRun: run));
        _boards.Setup(x => x.FinishAgentRun(run.Id, AgentRunStates.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _runner.RecoverInterruptedRun(CancellationToken.None);

        Assert.Null(RunsInProgress.Load(_runFile));
        _boards.Verify(x => x.FinishAgentRun(run.Id, AgentRunStates.Failed, "The runner stopped during the work", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task RecoverInterruptedRun_ShouldDoNothing_WhenNoRunWasInProgress()
    {
        await _runner.RecoverInterruptedRun(CancellationToken.None);

        _boards.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        _directory.Dispose();
    }
}
