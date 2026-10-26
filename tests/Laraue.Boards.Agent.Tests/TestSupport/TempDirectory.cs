namespace Laraue.Boards.Agent.Tests.TestSupport;

/// <summary>A new empty folder, deleted with everything in it when disposed.</summary>
sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory("lba-tests-").FullName;
    }

    public string Path { get; }

    public string Combine(params string[] parts)
    {
        return System.IO.Path.Combine([Path, .. parts]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind isn't a failure.
        }
    }
}
