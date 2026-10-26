using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Updates;

namespace Laraue.Boards.Agent.Commands;

/// <summary>
/// `lba update [version]`: installs the newest release (or the one named) in place of the running binary, as the
/// install script would, and restarts the background service on it. A run in progress resumes after the restart.
/// </summary>
public static class UpdateCommand
{
    public static async Task<int> RunAsync(RunnerArgs options, CancellationToken ct)
    {
        if (Service.StableExecutablePath() is not { } target)
        {
            Console.WriteLine("This laraue-boards-agent runs from source, so there's no installed binary to update. Use the install script, or `git pull`.");
            return 1;
        }

        using var http = new HttpClient();
        var updater = new Updater(http);
        var tag = options.UpdateVersion ?? await updater.NewestVersion(ct);
        if (options.UpdateVersion is null && !ReleaseVersions.IsNewer(tag, RunnerVersion.Value))
        {
            Console.WriteLine($"Already up to date ({RunnerVersion.Value}; the newest release is {tag}).");
            return 0;
        }

        var runtime = Updater.CurrentRuntime();
        Console.WriteLine($"Downloading laraue-boards-agent {tag} ({runtime})...");
        var folder = Directory.CreateTempSubdirectory("laraue-boards-agent-update-").FullName;
        try
        {
            var executable = await updater.Download(tag, runtime, folder, ct);
            Updater.Install(executable, target);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Console.WriteLine($"Installed {tag} as {target}.");
        RestartService(options.ConfigPath, target);
        return 0;
    }

    /// <summary>
    /// The running service still has the old binary: it restarts on the new one, as after the install script - if it runs
    /// this binary, not another copy.
    /// </summary>
    private static void RestartService(string configPath, string executable)
    {
        if (!Service.IsSupported || !Service.IsInstalled)
        {
            Console.WriteLine("Start the runner again (lba) to use the new version.");
            return;
        }

        if (!Service.RegisteredFor(executable))
        {
            Console.WriteLine("The background service runs another copy of laraue-boards-agent, so it isn't restarted. Update that copy with its own `lba update`.");
            return;
        }

        if (RunsInProgress.Load(RunsInProgress.PathFor(configPath)) is { } run)
        {
            Console.WriteLine($"{run.IssueKey}'s agent run is in progress: it resumes after the restart.");
        }

        Console.WriteLine("Restarting the background service...");
        Service.Restart();
        Console.WriteLine("The background service runs the new version.");
    }
}
