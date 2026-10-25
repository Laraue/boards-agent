namespace Laraue.Boards.Agent.Config;

/// <summary>What happens to the agent's work after a Done run, see <see cref="RunnerConfig.Publish"/>.</summary>
static class PublishModes
{
    public const string Off = "off";

    public const string Push = "push";

    public const string PullRequest = "pr";

    public static readonly string[] All = [Off, Push, PullRequest];
}
