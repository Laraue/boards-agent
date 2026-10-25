namespace Laraue.Boards.Agent.Boards;

/// <summary>Agent run states, as the MCP tools name them.</summary>
static class AgentRunStates
{
    public const string Ready = "Ready";
    public const string Working = "Working";
    public const string Done = "Done";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}
