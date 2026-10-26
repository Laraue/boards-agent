namespace Laraue.Boards.Agent.Config;

/// <summary>Whose pull request review comments the agent gets on a re-run, see <see cref="RunnerConfig.ReviewComments"/>.</summary>
public static class ReviewCommentLevels
{
    /// <summary>None: the runner doesn't read pull requests.</summary>
    public const string Off = "off";

    /// <summary>Only the repository's owner (a user, or the organization's owners).</summary>
    public const string Owner = "owner";

    /// <summary>The owner, the organization's members and the repository's collaborators - people with access to it.</summary>
    public const string Collaborators = "collaborators";

    /// <summary>Anyone who can comment: on a public repository, anyone with a GitHub account.</summary>
    public const string Anyone = "anyone";

    public static readonly string[] All = [Off, Owner, Collaborators, Anyone];

    /// <summary>GitHub's author associations trusted at <paramref name="level"/>; null when everyone is.</summary>
    public static string[]? Associations(string level)
    {
        return level switch
        {
            Owner => ["OWNER"],
            Anyone => null,
            _ => ["OWNER", "MEMBER", "COLLABORATOR"],
        };
    }

    /// <summary>The level and what it means, e.g. "owner - only the repository owner's review comments".</summary>
    public static string Describe(string level)
    {
        return level switch
        {
            Off => $"{Off} - pull requests aren't read",
            Owner => $"{Owner} - only the repository owner's review comments",
            Anyone => $"{Anyone} - anyone's review comments",
            _ => $"{Collaborators} - the owner's, members' and collaborators' review comments",
        };
    }
}
