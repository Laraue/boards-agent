namespace Laraue.Boards.Agent.Updates;

/// <summary>Release versions as tagged: v0.5.0, v0.5.0-beta.3. A release is newer than its betas; beta.10 is newer than beta.9.</summary>
public static class ReleaseVersions
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is newer than <paramref name="current"/>. A current version that isn't a
    /// release version (a build from source) is older than any release.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (Parse(candidate) is not { } next)
        {
            return false;
        }

        return Parse(current) is not { } now || Compare(next, now) > 0;
    }

    private sealed record ParsedVersion(Version Number, string[] PreRelease);

    private static ParsedVersion? Parse(string text)
    {
        var value = text.TrimStart('v', 'V');
        var dash = value.IndexOf('-');
        var number = dash < 0 ? value : value[..dash];
        if (!Version.TryParse(number, out var parsed))
        {
            return null;
        }

        return new ParsedVersion(parsed, dash < 0 ? [] : value[(dash + 1)..].Split('.'));
    }

    private static int Compare(ParsedVersion left, ParsedVersion right)
    {
        var byNumber = left.Number.CompareTo(right.Number);
        if (byNumber != 0)
        {
            return byNumber;
        }

        // No pre-release part is the release itself, newer than any of its pre-releases.
        if (left.PreRelease.Length == 0 || right.PreRelease.Length == 0)
        {
            return right.PreRelease.Length.CompareTo(left.PreRelease.Length);
        }

        for (var i = 0; i < Math.Min(left.PreRelease.Length, right.PreRelease.Length); i++)
        {
            var byPart = int.TryParse(left.PreRelease[i], out var l) && int.TryParse(right.PreRelease[i], out var r)
                ? l.CompareTo(r)
                : string.CompareOrdinal(left.PreRelease[i], right.PreRelease[i]);
            if (byPart != 0)
            {
                return byPart;
            }
        }

        return left.PreRelease.Length.CompareTo(right.PreRelease.Length);
    }
}
