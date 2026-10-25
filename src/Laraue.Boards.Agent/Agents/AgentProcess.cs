using System.Text.Json.Nodes;

namespace Laraue.Boards.Agent.Agents;

/// <summary>
/// A running agent working on one run: its process, with stdout and stderr written to the log line by line and stdout
/// read by the agent's <see cref="AgentOutput"/>.
/// </summary>
sealed class AgentProcess : IDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly StreamWriter _log;
    private readonly Task _pump;

    public AgentProcess(System.Diagnostics.Process process, string logPath, AgentOutput output)
    {
        _process = process;
        Output = output;
        _log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        _pump = Task.Run(Pump);
    }

    public AgentOutput Output { get; }

    public int ProcessId => _process.Id;

    public int ExitCode => _process.ExitCode;

    /// <inheritdoc cref="AgentOutput.SessionId"/>
    public string? SessionId => Output.SessionId;

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

    /// <inheritdoc cref="AgentOutput.Summary"/>
    public string Summary() => Output.Summary();

    /// <inheritdoc cref="AgentOutput.FinalMessage"/>
    public string? FinalMessage() => Output.FinalMessage();

    /// <inheritdoc cref="AgentOutput.HitTurnLimit"/>
    public bool HitTurnLimit => Output.HitTurnLimit;

    /// <inheritdoc cref="AgentOutput.OnStep"/>
    public Action<string>? OnStep
    {
        get => Output.OnStep;
        set => Output.OnStep = value;
    }

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

            Output.Read(line);
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
