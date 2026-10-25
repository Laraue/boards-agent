using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Agents;

/// <summary>
/// A coding agent the runner hands an issue to: it finds the agent's CLI, checks its sign-in, and starts it in the
/// issue's worktree with the Boards MCP server. Everything else - polling, claiming, the worktree, cancel and timeout -
/// doesn't depend on the agent.
/// </summary>
interface ICodingAgent
{
    /// <summary>The config value that picks this agent (<see cref="RunnerConfig.Agent"/>).</summary>
    string Key { get; }

    /// <summary>The agent's name in the log and in setup, e.g. "Claude Code".</summary>
    string Name { get; }

    /// <summary>How the runner introduces itself at the Boards sign-in - the start of the API key's name.</summary>
    string ClientName { get; }

    /// <summary>Where to get the agent when it isn't installed.</summary>
    string InstallUrl { get; }

    /// <summary>The agent's CLI. Throws <see cref="FileNotFoundException"/> when it isn't installed.</summary>
    string FindExecutable();

    bool IsSignedIn(string executable);

    /// <summary>Signs the agent in, in this terminal.</summary>
    void SignIn(string executable);

    /// <summary>Starts the agent on the run in its worktree, writing the agent's output to <paramref name="logPath"/>.</summary>
    AgentProcess Start(string apiKey, AgentRunSummary run, Workspace workspace, string logPath);

    /// <summary>
    /// `lba continue KEY "instruction"`: one more round of the agent's earlier session <paramref name="sessionId"/>, as
    /// unattended as a run (the same permissions and Boards tools), with <paramref name="instruction"/> as its prompt.
    /// </summary>
    AgentProcess Resume(string apiKey, string issueKey, Workspace workspace, string sessionId, string instruction, string logPath);

    /// <summary>
    /// `lba continue KEY`: the agent's earlier session, interactive in this terminal - the person talks to it and approves
    /// what it asks. <paramref name="context"/>, when given, tells the agent about the session (e.g. its agent run)
    /// without being a message of the person's. Returns its exit code.
    /// </summary>
    int ResumeInteractive(string apiKey, string issueKey, Workspace workspace, string sessionId, string? context);
}
