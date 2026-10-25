using System.Text.Json;
using Laraue.Boards.Agent.Agents;

namespace Laraue.Boards.Agent.Agents.Codex;

/// <summary>
/// Codex's <c>--json</c> output, one event per line: <c>turn.completed</c> carries the token usage, <c>turn.failed</c>
/// and <c>error</c> what went wrong, and an <c>item.completed</c> of type <c>agent_message</c> the final answer.
/// </summary>
sealed class CodexProcess(System.Diagnostics.Process process, string logPath) : AgentProcess(process, logPath)
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

    protected override void OnOutputLine(string line)
    {
        try
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "item.completed"
                    when root.TryGetProperty("item", out var item)
                        && item.TryGetProperty("type", out var itemType)
                        && itemType.GetString() == "agent_message":
                    _lastMessage = item.TryGetProperty("text", out var messageText) ? messageText.GetString() : _lastMessage;
                    break;
                case "item.started" when root.TryGetProperty("item", out var started):
                    Step(CodexStep(started));
                    break;
                case "item.completed"
                    when root.TryGetProperty("item", out var completed)
                        && completed.TryGetProperty("type", out var completedType)
                        && completedType.GetString() == "file_change":
                    Step(CodexStep(completed));
                    break;
                case "thread.started":
                    SessionId = root.TryGetProperty("thread_id", out var thread) ? thread.GetString() : null;
                    break;
                case "turn.completed":
                    _turns++;
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        _inputTokens += Number(usage, "input_tokens");
                        _outputTokens += Number(usage, "output_tokens");
                    }

                    break;
                case "turn.failed":
                    _turns++;
                    _error = ErrorMessage(root);
                    break;
                case "error":
                    _error = ErrorMessage(root);
                    break;
            }
        }
        catch (JsonException)
        {
            // Not an event line; it's in the log anyway.
        }
    }

    /// <summary>An item as a step: the command it runs, the files it changed, the Boards tool it calls.</summary>
    private static string? CodexStep(JsonElement item)
    {
        string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        switch (Text(item, "type"))
        {
            case "command_execution":
                // Codex wraps commands in a shell: bash -lc 'dotnet test'.
                var command = Text(item, "command");
                return command is not null && command.StartsWith("bash -lc ", StringComparison.Ordinal)
                    ? command["bash -lc ".Length..].Trim('\'', '"')
                    : command;
            case "file_change" when item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array:
                var files = changes.EnumerateArray()
                    .Select(change => Text(change, "path"))
                    .OfType<string>()
                    .Select(Path.GetFileName);
                return $"Edit {string.Join(", ", files)}";
            case "mcp_tool_call":
                var server = Text(item, "server");
                return server == "boards" ? $"Boards: {Text(item, "tool")}" : $"{server}: {Text(item, "tool")}";
            default:
                return null;
        }
    }

    private static long Number(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    }

    private static string ErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var nested))
        {
            return nested.GetString() ?? "Unknown error.";
        }

        return root.TryGetProperty("message", out var message) ? message.GetString() ?? "Unknown error." : "Unknown error.";
    }
}
