using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Agents.Codex;

/// <summary>
/// OpenAI's Codex CLI, as <c>codex exec</c>: the workspace-write sandbox (edits only inside the worktree, commands
/// without network unless <see cref="RunnerConfig.CodexNetworkAccess"/>), no approvals since nobody is there to give
/// them, web search off, and only the Boards MCP server - the person's own <c>~/.codex/config.toml</c> isn't loaded.
/// The work-on-issue skill is the same <c>SKILL.md</c> Claude Code uses. Codex has no turn or cost limit: the runner's
/// timeout is the only one.
/// </summary>
public sealed class CodexAgent(RunnerConfig config) : ICodingAgent
{
    public string Key => CodingAgents.Codex;

    public string Name => "Codex";

    public string ClientName => "Codex agent";

    public string InstallUrl => "https://developers.openai.com/codex/cli";

    public string FindExecutable()
    {
        if (config.CodexPath != "codex")
        {
            return Paths.Expand(config.CodexPath);
        }

        return CodingAgents.FindOnPath("codex")
            ?? throw new FileNotFoundException("Codex wasn't found: put codex on PATH or set codexPath in the config.");
    }

    /// <summary>Whether Codex is signed in, by <c>codex login status</c>'s exit code.</summary>
    public bool IsSignedIn(string executable)
    {
        return Shell.Run(executable, "login", "status").ExitCode == 0;
    }

    /// <summary><c>codex login</c> in this terminal - the person signs in in the browser it opens.</summary>
    public void SignIn(string executable)
    {
        var info = new System.Diagnostics.ProcessStartInfo(executable);
        info.ArgumentList.Add("login");
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
    }

    public AgentProcess Start(string apiKey, AgentRunSummary run, Workspace workspace, string logPath)
    {
        var info = Info(apiKey, workspace);
        AddArguments(info, ["exec", "--json", .. Options(workspace), CodingAgents.Prompt("work-on-issue", run, workspace)]);
        return new AgentProcess(AgentProcess.Launch(info), logPath, new CodexOutput());
    }

    public AgentProcess Resume(string apiKey, string issueKey, Workspace workspace, string sessionId, string instruction, string logPath)
    {
        var info = Info(apiKey, workspace);
        AddArguments(info, ["exec", "--json", .. Options(workspace), "resume", sessionId, instruction]);
        return new AgentProcess(AgentProcess.Launch(info), logPath, new CodexOutput());
    }

    public int ResumeInteractive(string apiKey, string issueKey, Workspace workspace, string sessionId, string? context)
    {
        // Codex can't take extra instructions without sending them as the person's first message, so the context isn't
        // passed: the runner ends the run itself when the session ends.
        var info = Info(apiKey, workspace);
        AddArguments(info, [.. Options(workspace), "resume", sessionId]);
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private System.Diagnostics.ProcessStartInfo Info(string apiKey, Workspace workspace)
    {
        var info = new System.Diagnostics.ProcessStartInfo(FindExecutable()) { WorkingDirectory = workspace.Path };

        // env_http_headers reads the key from this variable, so it's never on the command line or in a file.
        info.Environment[config.ApiKeyEnv] = apiKey;
        return info;
    }

    /// <summary>The sandbox, the Boards MCP server and skill, the workspace's folders - the same for a run and a resume.</summary>
    private List<string> Options(Workspace workspace)
    {
        var skill = Path.Combine(AgentPlugin.Directory.Value, "skills", "work-on-issue");
        var tools = string.Join(", ", CodingAgents.BoardsTools.Select(Toml));
        List<string> options =
        [
            // Not --ephemeral: `lba continue` resumes the session afterwards.
            "--ignore-user-config",
            "--sandbox", "workspace-write",
            "-C", workspace.Path,
            // A workspace of several repositories isn't a git repository itself.
            .. (workspace.IsMultiRepo && !workspace.InCheckouts ? new[] { "--skip-git-repo-check" } : []),
            // In checkout mode the other clones aren't under the first one, the sandbox's writable folder.
            .. (workspace.InCheckouts && workspace.IsMultiRepo
                ? new[] { "-c", $"sandbox_workspace_write.writable_roots=[{string.Join(", ", workspace.Repos.Skip(1).Select(repo => Toml(repo.Path)))}]" }
                : []),
            "-c", "approval_policy=\"never\"",
            "-c", "web_search=\"disabled\"",
            "-c", $"sandbox_workspace_write.network_access={(config.CodexNetworkAccess ? "true" : "false")}",
            // The key reaches the MCP connection, which Codex makes itself, but not the commands it runs.
            "-c", $"shell_environment_policy.exclude=[{Toml(config.ApiKeyEnv)}]",
            "-c", $"mcp_servers.boards.url={Toml(config.McpUrl)}",
            "-c", $"mcp_servers.boards.env_http_headers={{ \"X-Api-Key\" = {Toml(config.ApiKeyEnv)} }}",
            "-c", $"mcp_servers.boards.enabled_tools=[{tools}]",
            "-c", "mcp_servers.boards.required=true",
            "-c", $"skills.config=[{{ path = {Toml(skill)} }}]",
        ];
        if (config.CodexModel is { Length: > 0 } model)
        {
            options.AddRange(["-m", model]);
        }

        return options;
    }

    private static void AddArguments(System.Diagnostics.ProcessStartInfo info, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
    }

    /// <summary>A TOML basic string, for the values of <c>-c key=value</c>.</summary>
    private static string Toml(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
