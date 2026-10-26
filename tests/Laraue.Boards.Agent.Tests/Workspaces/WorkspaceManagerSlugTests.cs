using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Tests.Workspaces;

public class WorkspaceManagerSlugTests
{
    [Theory]
    [InlineData("Sort epics in space detail page", "sort-epics-in-space-detail-page")]
    [InlineData("  Fix: the «Café» button!  ", "fix-the-cafe-button")]
    [InlineData("Сортировка эпиков", "")]
    public void Slug_ShouldGiveLowerCaseAsciiWords_WhenGivenATitle(string title, string expected)
    {
        Assert.Equal(expected, WorkspaceManager.Slug(title));
    }

    [Fact]
    public void Slug_ShouldCutAtAWordBoundary_WhenTheTitleIsLong()
    {
        var slug = WorkspaceManager.Slug("Save documents sent or forwarded to the Telegram bot as issue attachments");

        Assert.Equal("save-documents-sent-or-forwarded-to-the", slug);
    }

    [Fact]
    public void ChooseBranch_ShouldBeTheKeyAndSlug_WhenTheSpaceHasNoRepositoryWithAnEarlierBranch()
    {
        var branch = WorkspaceManager.ChooseBranch(TestSupport.TestData.Space(), "BRD-42", "Sort epics");

        Assert.Equal(new IssueBranch("agent/BRD-42-sort-epics", MergedBranch: null), branch);
    }
}
