using System.Text.Json;
using Laraue.Boards.Agent.Agents;

namespace Laraue.Boards.Agent.Agents.ClaudeCode;

/// <summary>Claude Code's stream-json output: the final `result` line says how it ended, its turns and its cost.</summary>
sealed class ClaudeCodeProcess(System.Diagnostics.Process process, string logPath) : AgentProcess(process, logPath)
{
    private string? _resultLine;

    public override string Summary()
    {
        if (_resultLine is null)
        {
            return "No result from Claude Code.";
        }

        try
        {
            using var result = JsonDocument.Parse(_resultLine);
            var root = result.RootElement;
            var subtype = root.TryGetProperty("subtype", out var s) ? s.GetString() : null;
            var turns = root.TryGetProperty("num_turns", out var t) ? t.GetInt32() : 0;
            var cost = root.TryGetProperty("total_cost_usd", out var c) ? c.GetDecimal() : 0;
            var isError = root.TryGetProperty("is_error", out var e) && e.GetBoolean();
            var text = root.TryGetProperty("result", out var r) ? r.GetString() : null;
            var summary = $"Claude Code: {subtype}{(isError ? " (error)" : "")}, {turns} turns, ${cost:0.00}.";
            return isError && !string.IsNullOrEmpty(text) ? $"{summary} {text}" : summary;
        }
        catch (JsonException)
        {
            return "Claude Code's result couldn't be read.";
        }
    }

    public override bool HitTurnLimit => _resultLine?.Contains("\"error_max_turns\"", StringComparison.Ordinal) == true;

    public override string? FinalMessage()
    {
        if (_resultLine is null)
        {
            return null;
        }

        try
        {
            using var result = JsonDocument.Parse(_resultLine);
            return result.RootElement.TryGetProperty("result", out var text) ? text.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected override void OnOutputLine(string line)
    {
        if (IsResult(line))
        {
            _resultLine = line;
        }

        if (line.Contains("\"tool_use\"", StringComparison.Ordinal))
        {
            ReportToolCalls(line);
        }

        // Every stream-json message carries the session id; the first one is enough.
        if (SessionId is null && line.Contains("\"session_id\"", StringComparison.Ordinal))
        {
            try
            {
                using var message = JsonDocument.Parse(line);
                if (message.RootElement.TryGetProperty("session_id", out var id) && id.GetString() is { Length: > 0 } sessionId)
                {
                    SessionId = sessionId;
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    /// <summary>Each tool call of an assistant message as a step: "Build and run the tests", "Edit IssuesService.cs".</summary>
    private void ReportToolCalls(string line)
    {
        try
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.GetString() != "assistant"
                || !root.TryGetProperty("message", out var body)
                || !body.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var blockType)
                    && blockType.GetString() == "tool_use"
                    && block.TryGetProperty("name", out var name))
                {
                    Step(ToolStep(name.GetString() ?? "", block.TryGetProperty("input", out var input) ? input : default));
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string? ToolStep(string tool, JsonElement input)
    {
        string? Text(string property) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        return tool switch
        {
            "Bash" => Text("description") ?? Text("command"),
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" =>
                (Text("file_path") ?? Text("notebook_path")) is { } path ? $"{tool} {Path.GetFileName(path)}" : tool,
            "Grep" or "Glob" => $"Search {Text("pattern")}",
            "TodoWrite" => null,
            _ when tool.StartsWith("mcp__boards__", StringComparison.Ordinal) => $"Boards: {tool["mcp__boards__".Length..]}",
            _ => tool,
        };
    }

    private static bool IsResult(string line)
    {
        try
        {
            using var message = JsonDocument.Parse(line);
            return message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "result";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
