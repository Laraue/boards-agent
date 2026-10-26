namespace Laraue.Boards.Agent.Infrastructure;

public static class Log
{
    private static StreamWriter? _file;

    /// <summary>Also appends every line to <paramref name="path"/> - for the Windows logon task, which has no output redirection.</summary>
    public static void ToFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public static void Info(string message)
    {
        Write(Console.Out, $"{DateTime.Now:HH:mm:ss} {message}");
    }

    public static void Error(string message)
    {
        Write(Console.Error, $"{DateTime.Now:HH:mm:ss} ERROR {message}");
    }

    private static void Write(TextWriter console, string line)
    {
        console.WriteLine(line);
        if (_file is { } file)
        {
            lock (file)
            {
                file.WriteLine(line);
            }
        }
    }
}
