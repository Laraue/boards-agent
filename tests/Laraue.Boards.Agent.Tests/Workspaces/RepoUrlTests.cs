using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Tests.Workspaces;

public class RepoUrlTests
{
    [Theory]
    [InlineData("https://github.com/Acme/Web-App", "github.com/acme/web-app")]
    [InlineData("https://github.com/acme/web-app.git", "github.com/acme/web-app")]
    [InlineData("git@github.com:acme/web-app.git", "github.com/acme/web-app")]
    [InlineData("ssh://git@github.com/acme/web-app", "github.com/acme/web-app")]
    [InlineData("https://user:token@github.com/acme/web-app/", "github.com/acme/web-app")]
    [InlineData("https://github.com/acme/web-app?tab=readme#top", "github.com/acme/web-app")]
    public void Normalize_ShouldGiveHostOwnerAndName_WhenTheUrlIsInAnyGitForm(string url, string expected)
    {
        Assert.Equal(expected, RepoUrl.Normalize(url));
    }

    [Fact]
    public void Normalize_ShouldReturnNull_WhenTheTextHasNoPath()
    {
        Assert.Null(RepoUrl.Normalize("github.com"));
    }

    [Theory]
    [InlineData("github.com/acme/web-app", true)]
    [InlineData("github.com/acme/web-app/tree/main/docs", true)]
    [InlineData("github.com/acme/web-app-docs", false)]
    public void Contains_ShouldMatchTheRepositoryAndPagesUnderIt_WhenGivenALink(string link, bool expected)
    {
        Assert.Equal(expected, RepoUrl.Contains("github.com/acme/web-app", link));
    }
}
