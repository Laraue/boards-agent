using Laraue.Boards.Agent.Updates;

namespace Laraue.Boards.Agent.Tests.Updates;

public class ReleaseVersionsTests
{
    [Theory]
    [InlineData("v0.5.0-beta.3", "0.5.0-beta.2", true)]
    [InlineData("v0.5.0-beta.10", "0.5.0-beta.9", true)]
    [InlineData("v0.5.0", "0.5.0-beta.9", true)]
    [InlineData("v0.6.0-beta.1", "0.5.0", true)]
    [InlineData("v0.5.0-beta.3", "0.5.0-beta.3", false)]
    [InlineData("v0.5.0-beta.3", "0.5.0", false)]
    [InlineData("v0.4.0-beta.8", "0.5.0-beta.1", false)]
    [InlineData("v0.5.0", "dev", true)]
    [InlineData("nightly", "0.5.0", false)]
    public void IsNewer_ShouldOrderReleasesAndTheirBetas_WhenComparingVersions(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, ReleaseVersions.IsNewer(candidate, current));
    }
}
