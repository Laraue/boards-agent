// laraue-boards-agent (lba): picks up Laraue Boards issues handed to the agent and works on them with a coding agent.
// Run: dotnet run --project src/Laraue.Boards.Agent -- [setup] [--config <file>] [--once] [--dry-run] [--check] [--version]

using Laraue.Boards.Agent.Commands;
using Laraue.Boards.Agent.Infrastructure;

RunnerArgs options;
try
{
    options = RunnerArgs.Parse(args);
}
catch (ArgumentException e)
{
    // A typo isn't a crash: the message and the usage, no stack trace.
    Console.Error.WriteLine(e.Message);
    return 2;
}

if (options.LogPath is not null)
{
    Log.ToFile(options.LogPath);
}
if (options.Version)
{
    Console.WriteLine($"laraue-boards-agent {RunnerVersion.Value}");
    return 0;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

// launchd/systemd stop the runner with SIGTERM: stop like Ctrl+C, so a run in progress is failed, not left Working.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        stop.Cancel();
    });

if (options.Command == RunnerArgs.SetupCommand)
{
    try
    {
        return await Setup.RunAsync(options, stop.Token);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        return 1;
    }
}

if (options.Command == RunnerArgs.ServiceCommand)
{
    return Service.Run(options);
}

if (options.Command == RunnerArgs.ReviewCommentsCommand)
{
    try
    {
        return await ReviewCommentsCommand.RunAsync(options, stop.Token);
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);
        return 2;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        return 1;
    }
}

if (options.Command == RunnerArgs.UpdateCommand)
{
    try
    {
        return await UpdateCommand.RunAsync(options, stop.Token);
    }
    catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Couldn't update: {e.Message}");
        return 1;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        return 1;
    }
}

if (options.Command == RunnerArgs.ContinueCommand)
{
    try
    {
        return await ContinueCommand.RunAsync(options, stop.Token);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        return 1;
    }
}

return await RunCommand.RunAsync(options, stop.Token);
