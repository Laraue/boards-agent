using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Tests.TestSupport;
using Laraue.Boards.Agent.Workspaces;
using Moq;

namespace Laraue.Boards.Agent.Tests.Runs;

public class ReadyRunPollerTests : IDisposable
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly TempDirectory _directory = new();
    private readonly Mock<IBoardsClient> _boards = new();

    [Fact]
    public async Task PollOnce_ShouldLeaveTheRunForAnotherMachine_WhenNoRepositoryOfItsSpaceIsClonedHere()
    {
        var run = TestData.Run();
        _boards.Setup(x => x.ListReadyRuns(null, UserId, It.IsAny<CancellationToken>())).ReturnsAsync([run]);
        _boards.Setup(x => x.GetSpace("BRD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpaceDetail("BRD", "Boards", null, [new SpaceLinkSummary("https://github.com/acme/web-app", "The web app")]));

        var worked = await Poller().PollOnce(CancellationToken.None);

        Assert.False(worked);
        _boards.Verify(x => x.ClaimAgentRun(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_ShouldClaimNothing_WhenItsADryRun()
    {
        var git = new TestGit();
        try
        {
            git.Run(git.Clone, "remote", "set-url", "origin", "https://github.com/acme/web-app");
            var run = TestData.Run();
            _boards.Setup(x => x.ListReadyRuns(null, UserId, It.IsAny<CancellationToken>())).ReturnsAsync([run]);
            _boards.Setup(x => x.GetSpace("BRD", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpaceDetail("BRD", "Boards", null, [new SpaceLinkSummary("https://github.com/acme/web-app", "The web app")]));

            var worked = await Poller(codeFolder: Path.GetDirectoryName(git.Clone)!, dryRun: true).PollOnce(CancellationToken.None);

            Assert.False(worked);
            _boards.Verify(x => x.ClaimAgentRun(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            git.Dispose();
        }
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    private ReadyRunPoller Poller(string? codeFolder = null, bool dryRun = false)
    {
        var configPath = _directory.Combine("config.json");
        var config = new RunnerConfig
        {
            Notifications = false,
            WorktreeFolder = _directory.Path,
            CodeFolders = codeFolder is null ? [] : [codeFolder],
        };
        var runner = new Runner(_boards.Object, config, Mock.Of<ICodingAgent>(), "api-key", RunsInProgress.PathFor(configPath), configPath);
        return new ReadyRunPoller(_boards.Object, config, new SpaceResolver(_boards.Object, config, new CloneIndex(config.CodeFolders)), runner, UserId, dryRun);
    }
}
