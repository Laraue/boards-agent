namespace Laraue.Boards.Agent.Config;

/// <summary>Where the agent works, see <see cref="RunnerConfig.Workspace"/>.</summary>
static class WorkspaceModes
{
    public const string Worktree = "worktree";

    public const string Checkout = "checkout";

    public static readonly string[] All = [Worktree, Checkout];
}
