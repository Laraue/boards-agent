using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Tests.TestSupport;

namespace Laraue.Boards.Agent.Tests.Config;

public class RunnerConfigTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    [Fact]
    public void TryLoad_ShouldKeepTheDefaults_WhenTheConfigPredatesASetting()
    {
        var config = RunnerConfig.TryLoad(Write("""{ "codeFolders": ["~/Repos"] }"""))!;

        Assert.Equal(PublishModes.Off, config.Publish);
        Assert.Equal(ReviewCommentLevels.Collaborators, config.ReviewComments);
        Assert.Equal(RunnerConfig.DefaultMaxTurns, config.MaxTurns);
        Assert.True(config.Notifications);
    }

    [Fact]
    public void TryLoad_ShouldReadTheOldDefaultTurnLimitAsTheNewOne_WhenSetupWroteIt()
    {
        var config = RunnerConfig.TryLoad(Write("""{ "codeFolders": ["~/Repos"], "maxTurns": 50 }"""))!;

        Assert.Equal(RunnerConfig.DefaultMaxTurns, config.MaxTurns);
    }

    [Fact]
    public void TryLoad_ShouldReturnNull_WhenThereIsNoConfig()
    {
        Assert.Null(RunnerConfig.TryLoad(_directory.Combine("missing.json")));
    }

    [Fact]
    public void Load_ShouldThrow_WhenThereIsNoCodeFolder()
    {
        var path = Write("""{ "publish": "push" }""");

        var error = Assert.Throws<InvalidOperationException>(() => RunnerConfig.Load(path));
        Assert.Contains("lba setup", error.Message);
    }

    [Theory]
    [InlineData("""{ "codeFolders": ["~/Repos"], "publish": "always" }""", "unknown publish")]
    [InlineData("""{ "codeFolders": ["~/Repos"], "workspace": "cloud" }""", "unknown workspace")]
    [InlineData("""{ "codeFolders": ["~/Repos"], "reviewComments": "friends" }""", "unknown review comments level")]
    [InlineData("""{ "codeFolders": ["~/Repos"], "repositoryReviewComments": { "github.com/acme/api": "all" } }""", "unknown review comments level")]
    public void Load_ShouldThrow_WhenASettingHasAnUnknownValue(string json, string message)
    {
        var path = Write(json);

        var error = Assert.Throws<InvalidOperationException>(() => RunnerConfig.Load(path));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void ReviewCommentsFor_ShouldBeTheRepositorysOwnLevel_WhenOneOfItsRemotesHasOne()
    {
        var config = new RunnerConfig
        {
            ReviewComments = ReviewCommentLevels.Collaborators,
            RepositoryReviewComments = new Dictionary<string, string> { ["github.com/acme/api"] = "Anyone" },
        };

        Assert.Equal(ReviewCommentLevels.Anyone, config.ReviewCommentsFor(["github.com/acme/fork", "github.com/acme/api"]));
    }

    [Fact]
    public void ReviewCommentsFor_ShouldBeTheDefault_WhenNoRemoteHasItsOwnLevel()
    {
        var config = new RunnerConfig { ReviewComments = ReviewCommentLevels.Owner };

        Assert.Equal(ReviewCommentLevels.Owner, config.ReviewCommentsFor(["github.com/acme/web"]));
    }

    [Fact]
    public void Save_ShouldWriteWhatTryLoadReadsBack_WhenTheConfigChanged()
    {
        var path = _directory.Combine("config.json");
        new RunnerConfig { CodeFolders = ["/repos"], Publish = PublishModes.PullRequest, ReviewComments = ReviewCommentLevels.Off }.Save(path);

        var loaded = RunnerConfig.Load(path);

        Assert.Equal(PublishModes.PullRequest, loaded.Publish);
        Assert.Equal(ReviewCommentLevels.Off, loaded.ReviewComments);
        Assert.Equal(["/repos"], loaded.CodeFolders);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    private string Write(string json)
    {
        var path = _directory.Combine($"{Guid.NewGuid()}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
