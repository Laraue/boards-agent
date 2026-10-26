using System.Text.Json;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Agents.ClaudeCode;

/// <summary>
/// Claude Code's stream-json output: every message carries the session id, an <c>assistant</c> message its tool calls,
/// and the final <c>result</c> says how it ended, its turns and its cost.
/// </summary>
public sealed class ClaudeCodeOutput : AgentOutput
{
    private const string BoardsToolPrefix = "mcp__boards__";

    private bool _hasResult;
    private string? _subtype;
    private long _turns;
    private decimal _cost;
    private bool _isError;
    private string? _resultText;

    public override bool HitTurnLimit => _subtype == "error_max_turns";

    public override string Summary()
    {
        if (!_hasResult)
        {
            return "No result from Claude Code.";
        }

        var summary = $"Claude Code: {_subtype}{(_isError ? " (error)" : "")}, {_turns} turns, ${_cost:0.00}.";
        return _isError && !string.IsNullOrEmpty(_resultText) ? $"{summary} {_resultText}" : summary;
    }

    public override string? FinalMessage()
    {
        return _resultText;
    }

    protected override void Read(JsonElement message)
    {
        // Every stream-json message carries the session id; the first one is enough.
        SessionId ??= message.String("session_id") is { Length: > 0 } sessionId ? sessionId : null;

        switch (message.String("type"))
        {
            case "result":
                _hasResult = true;
                _subtype = message.String("subtype");
                _turns = message.Long("num_turns") ?? 0;
                _cost = message.Decimal("total_cost_usd") ?? 0;
                _isError = message.Bool("is_error");
                _resultText = message.String("result");
                break;
            case "assistant" when message.Property("message") is { } body:
                foreach (var block in body.Items("content").Where(x => x.String("type") == "tool_use"))
                {
                    Step(ToolStep(block.String("name") ?? "", block.Property("input") ?? default));
                }

                break;
        }
    }

    /// <summary>A tool call as a step: "Build and run the tests", "Edit IssuesService.cs", "Boards: create_comment".</summary>
    private static string? ToolStep(string tool, JsonElement input)
    {
        return tool switch
        {
            "Bash" => input.String("description") ?? input.String("command"),
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" =>
                (input.String("file_path") ?? input.String("notebook_path")) is { } path ? $"{tool} {Path.GetFileName(path)}" : tool,
            "Grep" or "Glob" => $"Search {input.String("pattern")}",
            "TodoWrite" => null,
            _ when tool.StartsWith(BoardsToolPrefix, StringComparison.Ordinal) => $"Boards: {tool[BoardsToolPrefix.Length..]}",
            _ => tool,
        };
    }
}
