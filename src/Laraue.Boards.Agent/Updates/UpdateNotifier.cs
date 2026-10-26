using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Updates;

/// <summary>When <see cref="UpdateNotifier"/> last looked for a release, and which one it told the person about.</summary>
public sealed record UpdateCheckState(DateTimeOffset CheckedAt, string? NotifiedVersion);

/// <summary>
/// The background service's look for a new release, once a day (<see cref="RunnerConfig.CheckForUpdates"/>): a newer one
/// is logged and shown as a notification once, pointing at `lba update`. It never updates by itself.
/// </summary>
/// <param name="statePath">Where the last check is kept, so a restart doesn't check (or notify) again.</param>
public sealed class UpdateNotifier(Updater updater, RunnerConfig config, string statePath, string currentVersion, TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>The state file beside the config file.</summary>
    public static string PathFor(string configPath)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "update-check.json");
    }

    /// <summary>Looks for a release when the last look was a day ago or more. The newer version it told about, else null.</summary>
    public async Task<string?> CheckIfDue(CancellationToken ct)
    {
        if (!config.CheckForUpdates)
        {
            return null;
        }

        var state = Load();
        var now = time.GetUtcNow();
        if (state is not null && now - state.CheckedAt < Interval)
        {
            return null;
        }

        string newest;
        try
        {
            newest = await updater.NewestVersion(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // GitHub not answering is no reason to stop working: the next check is in a day.
            Log.Info($"Couldn't look for a new version of laraue-boards-agent: {e.Message}");
            Save(new UpdateCheckState(now, state?.NotifiedVersion));
            return null;
        }

        var tell = ReleaseVersions.IsNewer(newest, currentVersion) && newest != state?.NotifiedVersion;
        Save(new UpdateCheckState(now, tell ? newest : state?.NotifiedVersion));
        if (!tell)
        {
            return null;
        }

        Log.Info($"laraue-boards-agent {newest} is available (this is {currentVersion}): run `lba update`. {Updater.ReleasePage(newest)}");
        Notifications.Show(config, $"laraue-boards-agent {newest} is available", "Run lba update in a terminal to install it.", Updater.ReleasePage(newest));
        return newest;
    }

    private UpdateCheckState? Load()
    {
        try
        {
            return File.Exists(statePath)
                ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(statePath), RunnerJson.Default.UpdateCheckState)
                : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void Save(UpdateCheckState state)
    {
        try
        {
            File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(state, RunnerJson.Default.UpdateCheckState));
        }
        catch (IOException e)
        {
            Log.Info($"Couldn't save {statePath}: {e.Message}");
        }
    }
}
