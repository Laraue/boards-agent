using System.Text.Json;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Runs;

namespace Laraue.Boards.Agent.Agents;

/// <summary>
/// The agent session of an issue's last run on this machine, for `lba continue` - only this machine has it. Kept in
/// sessions/KEY.json next to the config.
/// </summary>
/// <param name="ContinueRunId">
/// The run the last `lba continue` started (BRD-320). Still Working next time means that session was cut off (a crash,
/// a closed terminal), so the next `lba continue` carries on with it instead of starting another.
/// </param>
public sealed record AgentSession(string IssueKey, string IssueTitle, string IssueUrl, string Agent, string SessionId, RunInProgressWorkspace Workspace, long? ContinueRunId = null);

public static class AgentSessions
{
    /// <param name="besidePath">A file next to the config (e.g. the run record's): the sessions folder is beside it.</param>
    public static void Save(string besidePath, AgentSession session)
    {
        var path = PathFor(besidePath, session.IssueKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, RunnerJson.Default.AgentSession));
    }

    public static AgentSession? Load(string besidePath, string issueKey)
    {
        var path = PathFor(besidePath, issueKey);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), RunnerJson.Default.AgentSession) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Log.Error($"{path} can't be read: {e.Message}");
            return null;
        }
    }

    /// <summary>The session saved last, or null when there's none.</summary>
    public static AgentSession? Latest(string besidePath)
    {
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(besidePath))!, "sessions");
        var newest = Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault()
            : null;
        return newest is null ? null : Load(besidePath, Path.GetFileNameWithoutExtension(newest.Name));
    }

    private static string PathFor(string besidePath, string issueKey)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(besidePath))!, "sessions", $"{issueKey.ToUpperInvariant()}.json");
    }
}
