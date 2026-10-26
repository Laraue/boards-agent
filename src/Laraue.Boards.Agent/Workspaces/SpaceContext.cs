namespace Laraue.Boards.Agent.Workspaces;

/// <summary>A repository of a space, cloned on this machine.</summary>
/// <param name="Url">The space's link it was found by; null for a repository mapped by hand in the config.</param>
public sealed record SpaceRepo(string Name, string ClonePath, string BaseRef, string? Url, string? Description);

/// <summary>A link of the space that isn't a local repository: docs, designs, dashboards, or a repository not cloned here.</summary>
public sealed record SpaceLinkInfo(string Url, string Description);

/// <summary>What the runner knows about a space for a run: its description, its repositories here, its other links.</summary>
public sealed record SpaceContext(
    string Key,
    string? Name,
    string? Description,
    IReadOnlyList<SpaceRepo> Repos,
    IReadOnlyList<SpaceLinkInfo> OtherLinks);
