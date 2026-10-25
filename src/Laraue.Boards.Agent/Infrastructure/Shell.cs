namespace Laraue.Boards.Agent.Infrastructure;

sealed record ProcessResult(int ExitCode, string Output, string Error);

static class Shell
{
    /// <summary>Runs a process to the end, capturing its output; <paramref name="input"/> goes to its stdin.</summary>
    public static ProcessResult Run(System.Diagnostics.ProcessStartInfo info, string? input = null)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        using var process = System.Diagnostics.Process.Start(info)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
        }

        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error.Result);
    }

    public static ProcessResult Run(string file, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Run(info);
    }
}
