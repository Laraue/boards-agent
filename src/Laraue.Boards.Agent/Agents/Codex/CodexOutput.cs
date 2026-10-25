using System.Text.Json;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Agents.Codex;

/// <summary>
/// Codex's <c>--json</c> output, one event per line: <c>thread.started</c> names the session, <c>turn.completed</c>
/// carries the token usage, <c>turn.failed</c> and <c>error</c> what went wrong, and an <c>item.completed</c> of type
/// <c>agent_message</c> the final answer.
/// </summary>
sealed class CodexOutput : AgentOutput
{
    private int _turns;
    private string? _lastMessage;
    private long _inputTokens;
    private long _outputTokens;
    private string? _error;

    public override string? FinalMessage()
    {
        return _lastMessage;
    }

    public override string Summary()
    {
        var outcome = _error is null ? "completed" : "failed";
        var turns = _turns == 1 ? "1 turn" : $"{_turns} turns";
        var summary = $"Codex: {outcome}, {turns}, {_inputTokens + _outputTokens:N0} tokens ({_inputTokens:N0} in, {_outputTokens:N0} out).";
        return _error is null ? summary : $"{summary} {_error}";
    }

    protected override void Read(JsonElement message)
    {
        var item = message.Property("item");
        switch (message.String("type"))
        {
            case "item.started" when item is { } started:
                Step(ItemStep(started));
                break;
            case "item.completed" when item is { } completed && completed.String("type") == "agent_message":
                _lastMessage = completed.String("text") ?? _lastMessage;
                break;
            case "item.completed" when item is { } completed && completed.String("type") == "file_change":
                Step(ItemStep(completed));
                break;
            case "thread.started":
                SessionId = message.String("thread_id");
                break;
            case "turn.completed":
                _turns++;
                if (message.Property("usage") is { } usage)
                {
                    _inputTokens += usage.Long("input_tokens") ?? 0;
                    _outputTokens += usage.Long("output_tokens") ?? 0;
                }

                break;
            case "turn.failed":
                _turns++;
                _error = ErrorMessage(message);
                break;
            case "error":
                _error = ErrorMessage(message);
                break;
        }
    }

    /// <summary>An item as a step: the command it runs, the files it changed, the Boards tool it calls.</summary>
    private static string? ItemStep(JsonElement item)
    {
        switch (item.String("type"))
        {
            case "command_execution":
                // Codex wraps commands in a shell: bash -lc 'dotnet test'.
                var command = item.String("command");
                return command is not null && command.StartsWith("bash -lc ", StringComparison.Ordinal)
                    ? command["bash -lc ".Length..].Trim('\'', '"')
                    : command;
            case "file_change":
                var files = item.Items("changes")
                    .Select(change => change.String("path"))
                    .OfType<string>()
                    .Select(Path.GetFileName)
                    .ToList();
                return files.Count == 0 ? null : $"Edit {string.Join(", ", files)}";
            case "mcp_tool_call":
                var server = item.String("server");
                return server == "boards" ? $"Boards: {item.String("tool")}" : $"{server}: {item.String("tool")}";
            default:
                return null;
        }
    }

    private static string ErrorMessage(JsonElement message)
    {
        return message.Property("error")?.String("message") ?? message.String("message") ?? "Unknown error.";
    }
}
