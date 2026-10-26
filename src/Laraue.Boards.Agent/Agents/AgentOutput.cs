using System.Text.Json;

namespace Laraue.Boards.Agent.Agents;

/// <summary>
/// What an agent says on stdout, one JSON event per line, read as it comes: its session, its steps, and at the end how it
/// went (<see cref="Summary"/>) and its answer (<see cref="FinalMessage"/>). Each agent has its own format.
/// </summary>
public abstract class AgentOutput
{
    /// <summary>The agent's session, which `lba continue` resumes; null until the agent said it.</summary>
    public string? SessionId { get; protected set; }

    /// <summary>
    /// Called with one line per step the agent takes - a command, an edit, a Boards call - so `lba continue` with an
    /// instruction shows it working instead of a silent terminal.
    /// </summary>
    public Action<string>? OnStep { get; set; }

    /// <summary>The agent stopped at its turn limit (Claude Code's error_max_turns), not because it was done or stuck.</summary>
    public virtual bool HitTurnLimit => false;

    /// <summary>How the agent ended, for the log and the issue comment: outcome, turns, cost.</summary>
    public abstract string Summary();

    /// <summary>The agent's last message to the person - its answer; null when it said nothing.</summary>
    public abstract string? FinalMessage();

    /// <summary>One line of the agent's stdout. A line that isn't JSON is skipped: it's in the log anyway.</summary>
    public void Read(string line)
    {
        JsonDocument message;
        try
        {
            message = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (message)
        {
            Read(message.RootElement);
        }
    }

    protected abstract void Read(JsonElement message);

    protected void Step(string? text)
    {
        if (OnStep is { } onStep && text?.Split('\n')[0].Trim() is { Length: > 0 } line)
        {
            onStep(line.Length > 120 ? line[..119] + "…" : line);
        }
    }
}
