using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Tests.TestSupport;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Tests.Workspaces;

/// <summary>An issue that comes back after its branch was merged gets a new branch from the base, not the merged one.</summary>
public class MergedBranchTests : IDisposable
{
    private const string Branch = "agent/BRD-42-sort-epics";

    private readonly TestGit _git = new();
    private readonly TempDirectory _worktrees = new();

    private SpaceContext Space => TestData.Space(new SpaceRepo("clone", _git.Clone, "origin/main", "https://github.com/acme/clone", null));

    [Fact]
    public void ChooseBranch_ShouldContinueTheBranch_WhenItHasCommitsNotMergedYet()
    {
        WorkOnTheIssue();

        Assert.Equal(new IssueBranch(Branch, MergedBranch: null), WorkspaceManager.ChooseBranch(Space, "BRD-42", "Sort epics"));
    }

    [Fact]
    public void ChooseBranch_ShouldContinueTheBranch_WhenNobodyCommittedToItYet()
    {
        _git.Run(_git.Clone, "branch", Branch, "origin/main");

        Assert.Equal(new IssueBranch(Branch, MergedBranch: null), WorkspaceManager.ChooseBranch(Space, "BRD-42", "Sort epics"));
    }

    [Fact]
    public void ChooseBranch_ShouldStartANumberedBranch_WhenTheIssuesBranchWasMerged()
    {
        WorkOnTheIssue();
        MergeIntoMain(Branch);

        Assert.Equal(new IssueBranch($"{Branch}-2", MergedBranch: Branch), WorkspaceManager.ChooseBranch(Space, "BRD-42", "Sort epics"));
    }

    [Fact]
    public void CreateInCheckouts_ShouldStartFromTheBaseWithTheMergedWork_WhenTheIssuesBranchWasMerged()
    {
        WorkOnTheIssue();
        MergeIntoMain(Branch);

        var workspace = WorkspaceManager.CreateInCheckouts(Space, "BRD-42", "Sort epics");

        Assert.Equal($"{Branch}-2", Git.CurrentBranch(_git.Clone));
        Assert.Equal(Branch, workspace.MergedBranch);
        Assert.Equal(0, workspace.CommitsSoFar);
        Assert.True(File.Exists(Path.Combine(_git.Clone, "fix.txt")));
    }

    [Fact]
    public void ChooseBranch_ShouldContinueTheNewBranch_WhenTheNextRunComesAfterItGotCommits()
    {
        WorkOnTheIssue();
        MergeIntoMain(Branch);
        WorkspaceManager.CreateInCheckouts(Space, "BRD-42", "Sort epics");
        _git.Commit(_git.Clone, "more.txt", "more");
        _git.Run(_git.Clone, "switch", "main");

        Assert.Equal(new IssueBranch($"{Branch}-2", MergedBranch: null), WorkspaceManager.ChooseBranch(Space, "BRD-42", "Sort epics"));
    }

    [Fact]
    public void Create_ShouldReplaceTheMergedBranchesWorktree_WhenTheIssueCameBack()
    {
        var first = WorkspaceManager.Create(Space, _worktrees.Path, "BRD-42", "Sort epics");
        _git.Commit(first.Path, "fix.txt", "fixed");
        _git.Run(first.Path, "push", "origin", Branch);
        MergeIntoMain(Branch);

        var second = WorkspaceManager.Create(Space, _worktrees.Path, "BRD-42", "Sort epics");

        Assert.Equal(first.Path, second.Path);
        Assert.Equal($"{Branch}-2", Git.CurrentBranch(second.Path));
        Assert.Equal(Branch, second.MergedBranch);
    }

    public void Dispose()
    {
        _git.Dispose();
        _worktrees.Dispose();
    }

    /// <summary>An earlier run: the issue's branch from main with one commit, the clone back on main.</summary>
    private void WorkOnTheIssue()
    {
        _git.Run(_git.Clone, "switch", "-c", Branch, "origin/main");
        _git.Commit(_git.Clone, "fix.txt", "fixed");
        _git.Run(_git.Clone, "switch", "main");
    }

    /// <summary>The pull request merged on the remote: a merge commit on origin's main.</summary>
    private void MergeIntoMain(string branch)
    {
        _git.Run(_git.Clone, "switch", "main");
        _git.Run(_git.Clone, "merge", "--no-ff", "-m", $"Merge {branch}", branch);
        _git.Run(_git.Clone, "push", "origin", "main");
        _git.Run(_git.Clone, "reset", "--hard", "HEAD~1");
    }
}
