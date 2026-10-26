using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Workspaces;

/// <summary>A git checkout found on this machine, with its remotes' normalized URLs.</summary>
public sealed record LocalClone(string Path, IReadOnlyList<string> Remotes);

/// <summary>
/// The git checkouts under the configured code folders (up to three levels deep), found again every ten minutes so a
/// repository cloned meanwhile is picked up. Worktrees are skipped: only main checkouts count.
/// </summary>
public sealed class CloneIndex(IReadOnlyList<string> codeFolders)
{
    private const int MaxDepth = 3;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> SkippedFolders = ["node_modules", "bin", "obj", "dist", "build", "target", "vendor"];

    private IReadOnlyList<LocalClone> _clones = [];
    private DateTime _scannedAt = DateTime.MinValue;

    public IReadOnlyList<LocalClone> Clones
    {
        get
        {
            if (DateTime.UtcNow - _scannedAt > MaxAge)
            {
                _clones = Scan();
                _scannedAt = DateTime.UtcNow;
            }

            return _clones;
        }
    }

    /// <summary>The clone a link points into, or null. Several clones of one repository: the shortest path wins.</summary>
    public LocalClone? Find(string url)
    {
        var link = RepoUrl.Normalize(url);
        return link is null
            ? null
            : Clones
                .Where(clone => clone.Remotes.Any(remote => RepoUrl.Contains(remote, link)))
                .OrderBy(clone => clone.Path.Length)
                .FirstOrDefault();
    }

    private IReadOnlyList<LocalClone> Scan()
    {
        var clones = new List<LocalClone>();
        foreach (var folder in codeFolders.Select(Paths.Expand).Where(Directory.Exists).Distinct())
        {
            Walk(folder, 0, clones);
        }

        return clones;
    }

    private static void Walk(string folder, int depth, List<LocalClone> clones)
    {
        var dotGit = System.IO.Path.Combine(folder, ".git");
        if (Directory.Exists(dotGit))
        {
            clones.Add(new LocalClone(folder, Git.RemoteUrls(folder)));
            return;
        }

        // A .git file is a worktree (or submodule) of a checkout found elsewhere.
        if (File.Exists(dotGit) || depth >= MaxDepth)
        {
            return;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(folder);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var child in children)
        {
            var name = System.IO.Path.GetFileName(child);
            if (!name.StartsWith('.') && !SkippedFolders.Contains(name))
            {
                Walk(child, depth + 1, clones);
            }
        }
    }
}
