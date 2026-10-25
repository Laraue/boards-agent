using System.Text.Json;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Runs;

/// <summary>The run this machine is working on, see <see cref="RunsInProgress"/>.</summary>
/// <param name="Workspace">Null until the workspace exists.</param>
/// <param name="SessionId">The agent's session, as soon as it said it - what a restarted runner resumes.</param>
/// <param name="Resumes">How many times the run was already resumed after the runner stopped.</param>
sealed record RunInProgress(long RunId, string IssueKey, RunInProgressWorkspace? Workspace, string? SessionId = null, int Resumes = 0);

/// <summary>What <see cref="WorkspaceManager.Finish"/> needs of a <see cref="Workspace"/>, to put it back after a crash.</summary>
sealed record RunInProgressWorkspace(string Path, string Branch, bool InCheckouts, IReadOnlyList<WorkspaceRepo> Repos)
{
    public static RunInProgressWorkspace Of(Workspace workspace)
    {
        return new RunInProgressWorkspace(workspace.Path, workspace.Branch, workspace.InCheckouts, workspace.Repos);
    }

    public Workspace ToWorkspace(string issueKey)
    {
        var space = new SpaceContext(IssueKeys.SpaceKey(issueKey), Name: null, Description: null, Repos: [], OtherLinks: []);
        return new Workspace(Path, Branch, Repos, space, InCheckouts);
    }
}

/// <summary>
/// The run in progress, in a file next to the config (current-run.json): written when a run is claimed, removed when
/// it ends. Still there when the runner starts means the last run was cut off (<see cref="Runner.RecoverInterruptedRun"/>).
/// </summary>
static class RunsInProgress
{
    public static string PathFor(string configPath)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "current-run.json");
    }

    public static void Save(string path, RunInProgress run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(run, RunnerJson.Default.RunInProgress));
    }

    /// <summary>The recorded run, or null when there's none or the file can't be read.</summary>
    public static RunInProgress? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), RunnerJson.Default.RunInProgress)
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Log.Error($"{path} can't be read ({e.Message}) - ignoring it.");
            Clear(path);
            return null;
        }
    }

    public static void Clear(string path)
    {
        File.Delete(path);
    }
}
