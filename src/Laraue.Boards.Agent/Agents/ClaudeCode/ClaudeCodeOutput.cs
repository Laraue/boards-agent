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

        var turns = _turns == 1 ? "1 turn" : $"{_turns} turns";
        var summary = $"Claude Code: {Outcome()}, {turns}, ${_cost:0.00}.";
        if (!Failed || string.IsNullOrEmpty(_resultText))
        {
            return summary;
        }

        summary = $"{summary} {_resultText.TrimEnd('.')}.";
        return IsSignInError(_resultText) ? $"{summary} {SignInTip}" : summary;
    }

    /// <summary>What to do when Claude Code's sign-in expired: the runner's runs use the same sign-in.</summary>
    public const string SignInTip = "To sign Claude Code in again, run `claude auth login` on this computer.";

    /// <summary>
    /// Claude Code's subtype alone can't say whether it failed: a failed sign-in ends with subtype "success" and
    /// is_error true (BRD-375).
    /// </summary>
    private bool Failed => _isError || _subtype?.StartsWith("error", StringComparison.Ordinal) == true;

    /// <summary>"completed", "failed", or "failed (out of turns)" for the error subtypes Claude Code documents.</summary>
    private string Outcome()
    {
        if (!Failed)
        {
            return "completed";
        }

        return _subtype switch
        {
            "error_max_turns" => "failed (out of turns)",
            "error_during_execution" => "failed (error during execution)",
            "error_max_budget_usd" => "failed (out of budget)",
            _ => "failed",
        };
    }

    /// <summary>The errors that a new sign-in fixes, e.g. "Failed to authenticate: OAuth session expired..." or "Please run /login".</summary>
    private static bool IsSignInError(string text)
    {
        return text.Contains("authenticate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
            || text.Contains("/login", StringComparison.Ordinal);
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
