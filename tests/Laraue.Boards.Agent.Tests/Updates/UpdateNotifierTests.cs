using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Tests.TestSupport;
using Laraue.Boards.Agent.Updates;
using Microsoft.Extensions.Time.Testing;

namespace Laraue.Boards.Agent.Tests.Updates;

public class UpdateNotifierTests : IDisposable
{
    private readonly FakeReleaseServer _server = new();
    private readonly TempDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task CheckIfDue_ShouldTellAboutTheNewerRelease_WhenOneIsOut()
    {
        _server.ListReleases("v0.5.0-beta.3");

        Assert.Equal("v0.5.0-beta.3", await Notifier().CheckIfDue(CancellationToken.None));
    }

    [Fact]
    public async Task CheckIfDue_ShouldTellNothing_WhenThisIsTheNewestRelease()
    {
        _server.ListReleases("v0.5.0-beta.2");

        Assert.Null(await Notifier().CheckIfDue(CancellationToken.None));
    }

    [Fact]
    public async Task CheckIfDue_ShouldNotAskAgain_WhenADayHasntPassedEvenAfterARestart()
    {
        _server.ListReleases("v0.5.0-beta.2");
        await Notifier().CheckIfDue(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(23));
        _server.ListReleases("v0.5.0-beta.3");

        Assert.Null(await Notifier().CheckIfDue(CancellationToken.None));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task CheckIfDue_ShouldTellAboutAReleaseOnce_WhenTheNextDayFindsTheSameOne()
    {
        _server.ListReleases("v0.5.0-beta.3");
        await Notifier().CheckIfDue(CancellationToken.None);
        _time.Advance(UpdateNotifier.Interval);

        Assert.Null(await Notifier().CheckIfDue(CancellationToken.None));
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task CheckIfDue_ShouldWaitADay_WhenGitHubDoesntAnswer()
    {
        _server.Fail();
        Assert.Null(await Notifier().CheckIfDue(CancellationToken.None));
        _server.ListReleases("v0.5.0-beta.3");

        Assert.Null(await Notifier().CheckIfDue(CancellationToken.None));
        _time.Advance(UpdateNotifier.Interval);
        Assert.Equal("v0.5.0-beta.3", await Notifier().CheckIfDue(CancellationToken.None));
    }

    [Fact]
    public async Task CheckIfDue_ShouldNotAskGitHub_WhenChecksAreOff()
    {
        _server.ListReleases("v0.5.0-beta.3");

        Assert.Null(await Notifier(checkForUpdates: false).CheckIfDue(CancellationToken.None));
        Assert.Empty(_server.Requests);
    }

    public void Dispose()
    {
        _directory.Dispose();
        _server.Dispose();
    }

    /// <summary>A new notifier each time, as after a restart of the service: only the state file carries over.</summary>
    private UpdateNotifier Notifier(bool checkForUpdates = true)
    {
        var config = new RunnerConfig { Notifications = false, CheckForUpdates = checkForUpdates };
        var updater = new Updater(new HttpClient(_server, disposeHandler: false), FakeReleaseServer.ApiUrl, FakeReleaseServer.DownloadUrl);
        return new UpdateNotifier(updater, config, UpdateNotifier.PathFor(_directory.Combine("config.json")), "0.5.0-beta.2", _time);
    }
}
