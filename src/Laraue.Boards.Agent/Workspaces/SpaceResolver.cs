using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Workspaces;

/// <summary>
/// Finds a space's repositories on this machine (BRD-312): every link of the space that points into a local clone, plus
/// a checkout mapped by hand in the config. Read from Boards at run time, so a link added there counts at the next
/// hand-off without running setup again.
/// </summary>
public sealed class SpaceResolver(IBoardsClient boards, RunnerConfig config, CloneIndex clones)
{
    public async Task<SpaceContext> Resolve(string spaceKey, CancellationToken ct)
    {
        SpaceDetail? detail;
        try
        {
            detail = await boards.GetSpace(spaceKey, ct);
        }
        catch (BoardsToolException)
        {
            // A Boards without get_space, or a space this key can't read: only a hand-mapped repository counts.
            detail = null;
        }

        var repos = new List<SpaceRepo>();
        var others = new List<SpaceLinkInfo>();
        foreach (var link in detail?.Links ?? [])
        {
            var clone = clones.Find(link.Url);
            if (clone is null)
            {
                others.Add(new SpaceLinkInfo(link.Url, link.Description));
            }
            else if (repos.All(x => x.ClonePath != clone.Path))
            {
                repos.Add(new SpaceRepo(UniqueName(repos, clone.Path), clone.Path, Git.DefaultBaseRef(clone.Path), link.Url, link.Description));
            }
        }

        if (config.Spaces.TryGetValue(spaceKey, out var manual)
            && manual.Repo is { Length: > 0 } repo
            && repos.All(x => x.ClonePath != repo))
        {
            repos.Add(new SpaceRepo(UniqueName(repos, repo), repo, manual.BaseRef, Url: null, Description: null));
        }

        return new SpaceContext(spaceKey, detail?.Name, detail?.Description, repos, others);
    }

    /// <summary>The clone's folder name, made unique within the workspace.</summary>
    private static string UniqueName(List<SpaceRepo> repos, string clonePath)
    {
        var name = Path.GetFileName(clonePath.TrimEnd(Path.DirectorySeparatorChar));
        var unique = name;
        for (var i = 2; repos.Any(x => x.Name == unique); i++)
        {
            unique = $"{name}-{i}";
        }

        return unique;
    }
}
