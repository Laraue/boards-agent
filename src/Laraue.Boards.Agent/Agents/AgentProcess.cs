using System.Text.Json.Nodes;

namespace Laraue.Boards.Agent.Agents;

/// <summary>
/// A running agent working on one run. The process and its output are handled here - stdout goes to the log line by line,
/// stderr too - and each agent reads its own output for the summary.
/// </summary>
abstract class AgentProcess : IDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly StreamWriter _log;
    private readonly Task _pump;

    protected AgentProcess(System.Diagnostics.Process process, string logPath)
    {
        _process = process;
        _log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        _pump = Task.Run(Pump);
    }

    public int ProcessId => _process.Id;

    public int ExitCode => _process.ExitCode;

    /// <summary>The agent's session, which `lba continue` resumes; null until the agent said it.</summary>
    public string? SessionId { get; protected set; }

    /// <summary>Starts an agent's process with its output captured and no input.</summary>
    public static System.Diagnostics.Process Launch(System.Diagnostics.ProcessStartInfo info)
    {
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Close();
        return process;
    }

    public Task WaitForExitAsync()
    {
        return Task.WhenAll(_process.WaitForExitAsync(), _pump);
    }

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.WaitForExit();
    }

    /// <summary>How the agent ended, for the log and the issue comment: outcome, turns, cost.</summary>
    public abstract string Summary();

    /// <summary>The agent's last message to the person - its answer; null when it said nothing.</summary>
    public abstract string? FinalMessage();

    /// <summary>The agent stopped at its turn limit (Claude Code's error_max_turns), not because it was done or stuck.</summary>
    public virtual bool HitTurnLimit => false;

    /// <summary>
    /// Called with one line per step the agent takes - a command, an edit, a Boards call - so `lba continue` with an
    /// instruction shows it working instead of a silent terminal.
    /// </summary>
    public Action<string>? OnStep { get; set; }

    protected void Step(string? text)
    {
        if (OnStep is { } onStep && text?.Split('\n')[0].Trim() is { Length: > 0 } line)
        {
            onStep(line.Length > 120 ? line[..119] + "…" : line);
        }
    }

    /// <summary>One line of the agent's stdout, already written to the log.</summary>
    protected abstract void OnOutputLine(string line);

    private async Task Pump()
    {
        var stderr = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                lock (_log)
                {
                    _log.WriteLine(new JsonObject { ["type"] = "stderr", ["text"] = line }.ToJsonString());
                }
            }
        });

        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            lock (_log)
            {
                _log.WriteLine(line);
            }

            OnOutputLine(line);
        }

        await stderr;
    }

    public void Dispose()
    {
        Kill();
        _pump.Wait(TimeSpan.FromSeconds(5));
        _log.Dispose();
        _process.Dispose();
    }
}
