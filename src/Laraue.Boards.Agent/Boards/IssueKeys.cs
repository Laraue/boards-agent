namespace Laraue.Boards.Agent.Boards;

static class IssueKeys
{
    /// <summary>Whether <paramref name="text"/> looks like an issue key, e.g. BRD-42 (any case).</summary>
    public static bool IsKey(string text)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Za-z][A-Za-z0-9]*-[0-9]+$");
    }

    /// <summary>The space part of an issue key: BRD for BRD-42.</summary>
    public static string SpaceKey(string issueKey)
    {
        var dash = issueKey.LastIndexOf('-');
        return dash > 0 ? issueKey[..dash] : issueKey;
    }
}
