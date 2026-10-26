using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Agents;

/// <summary>
/// The work-on-issue skill, embedded in the runner and written out (again) on start: a Claude Code plugin folder, whose
/// <c>skills/work-on-issue</c> Codex loads as a skill too.
/// </summary>
public static class AgentPlugin
{
    private const string Prefix = "plugin/";

    public static readonly Lazy<string> Directory = new(Extract);

    private static string Extract()
    {
        var assembly = typeof(AgentPlugin).Assembly;
        var target = Path.Combine(Path.GetTempPath(), $"boards-agent-plugin-{RunnerVersion.Value}");
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var relative = name.Replace('\\', '/');
            if (!relative.StartsWith(Prefix))
            {
                continue;
            }

            var path = Path.Combine(target, relative[Prefix.Length..]);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var resource = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(path);
            resource.CopyTo(file);
        }

        return File.Exists(Path.Combine(target, ".claude-plugin", "plugin.json"))
            ? target
            : throw new InvalidOperationException("The agent skill isn't embedded in this build of boards-agent.");
    }
}
