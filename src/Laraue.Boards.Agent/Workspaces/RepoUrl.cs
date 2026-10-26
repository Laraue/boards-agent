namespace Laraue.Boards.Agent.Workspaces;

/// <summary>
/// A repository URL reduced to <c>host/owner/repo</c>, lower-case, without scheme, user, <c>.git</c> or trailing slash, so
/// a git remote (<c>git@github.com:Laraue/x.git</c>) and a link (<c>https://github.com/Laraue/x</c>) compare equal.
/// </summary>
public static class RepoUrl
{
    public static string? Normalize(string url)
    {
        var value = url.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }
        else if (value.IndexOf(':') is var colon and > 0 && !value[..colon].Contains('/'))
        {
            // scp-like: git@github.com:owner/repo.git
            value = value[..colon] + "/" + value[(colon + 1)..];
        }

        var at = value.IndexOf('@');
        if (at >= 0 && at < value.IndexOf('/'))
        {
            value = value[(at + 1)..];
        }

        value = value.Split('?', '#')[0].TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^".git".Length];
        }

        return value.Contains('/') ? value.ToLowerInvariant() : null;
    }

    /// <summary>Whether a link points into the repository: the repository itself, or a page under it (<c>.../tree/main/docs</c>).</summary>
    public static bool Contains(string repository, string link)
    {
        return link == repository || link.StartsWith(repository + "/", StringComparison.Ordinal);
    }
}
