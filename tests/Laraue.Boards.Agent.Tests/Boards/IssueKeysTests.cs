using Laraue.Boards.Agent.Boards;

namespace Laraue.Boards.Agent.Tests.Boards;

public class IssueKeysTests
{
    [Theory]
    [InlineData("BRD-42", true)]
    [InlineData("min-21", true)]
    [InlineData("A1B-7", true)]
    [InlineData("BRD", false)]
    [InlineData("42-BRD", false)]
    [InlineData("print", false)]
    public void IsKey_ShouldRecognizeIssueKeys_WhenGivenText(string text, bool expected)
    {
        Assert.Equal(expected, IssueKeys.IsKey(text));
    }

    [Fact]
    public void SpaceKey_ShouldBeThePartBeforeTheNumber_WhenGivenAnIssueKey()
    {
        Assert.Equal("BRD", IssueKeys.SpaceKey("BRD-42"));
    }
}
