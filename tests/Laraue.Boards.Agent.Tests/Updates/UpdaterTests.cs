using Laraue.Boards.Agent.Tests.TestSupport;
using Laraue.Boards.Agent.Updates;

namespace Laraue.Boards.Agent.Tests.Updates;

public class UpdaterTests : IDisposable
{
    private readonly FakeReleaseServer _server = new();
    private readonly TempDirectory _directory = new();
    private readonly Updater _updater;

    public UpdaterTests()
    {
        _updater = new Updater(new HttpClient(_server), FakeReleaseServer.ApiUrl, FakeReleaseServer.DownloadUrl);
    }

    [Fact]
    public async Task NewestVersion_ShouldBeTheFirstListedRelease_WhenPreReleasesAreListed()
    {
        _server.ListReleases("v0.5.0-beta.3", "v0.5.0-beta.2");

        Assert.Equal("v0.5.0-beta.3", await _updater.NewestVersion(CancellationToken.None));
        Assert.NotEmpty(_server.Requests[0].Headers.UserAgent);
    }

    [Fact]
    public async Task Download_ShouldUnpackTheExecutable_WhenTheChecksumMatches()
    {
        _server.Publish("v0.5.0-beta.3", "linux-x64", "new binary");

        var executable = await _updater.Download("v0.5.0-beta.3", "linux-x64", _directory.Combine("download"), CancellationToken.None);

        Assert.Equal("new binary", await File.ReadAllTextAsync(executable));
    }

    [Fact]
    public async Task Download_ShouldRefuse_WhenTheChecksumDoesntMatch()
    {
        _server.Publish("v0.5.0-beta.3", "linux-x64", "tampered", wrongChecksum: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _updater.Download("v0.5.0-beta.3", "linux-x64", _directory.Combine("download"), CancellationToken.None));
        Assert.Contains("checksum", error.Message);
        Assert.False(Directory.Exists(_directory.Combine("download", "laraue-boards-agent")));
    }

    [Fact]
    public async Task Download_ShouldFail_WhenTheReleaseHasNoBuildForThisSystem()
    {
        _server.Publish("v0.5.0-beta.3", "linux-x64", "new binary");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => _updater.Download("v0.5.0-beta.3", "osx-arm64", _directory.Combine("download"), CancellationToken.None));
    }

    [Fact]
    public void Install_ShouldReplaceTheTargetWithAnExecutableCopy_WhenTheTargetExists()
    {
        var target = _directory.Combine("laraue-boards-agent");
        var downloaded = _directory.Combine("new");
        File.WriteAllText(target, "old binary");
        File.WriteAllText(downloaded, "new binary");

        Updater.Install(downloaded, target);

        Assert.Equal("new binary", File.ReadAllText(target));
        Assert.False(File.Exists(_directory.Combine(".laraue-boards-agent.new")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
        }
    }

    public void Dispose()
    {
        _directory.Dispose();
        _server.Dispose();
    }
}
