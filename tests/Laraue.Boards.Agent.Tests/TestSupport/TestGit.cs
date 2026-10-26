using System.Diagnostics;

namespace Laraue.Boards.Agent.Tests.TestSupport;

/// <summary>
/// A local repository setup with git: a bare <c>origin</c> with one commit on <c>main</c>, and a clone of it, as on the
/// person's machine. Nothing leaves the temp folder.
/// </summary>
sealed class TestGit : IDisposable
{
    private readonly TempDirectory _directory = new();

    public TestGit()
    {
        Origin = _directory.Combine("origin.git");
        Clone = _directory.Combine("clone");
        Run(_directory.Path, "init", "--bare", "--initial-branch=main", Origin);
        Run(_directory.Path, "clone", Origin, Clone);
        Commit(Clone, "README.md", "first");
        Run(Clone, "push", "origin", "main");
    }

    /// <summary>The bare repository the clone pushes to.</summary>
    public string Origin { get; }

    /// <summary>The clone, on main.</summary>
    public string Clone { get; }

    public string Head(string repo, string reference = "HEAD")
    {
        return Run(repo, "rev-parse", reference).Trim();
    }

    public void Commit(string repo, string file, string content)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Run(repo, "add", file);
        Run(repo, "commit", "-m", $"Change {file}");
    }

    public bool BranchExists(string repo, string branch)
    {
        return TryRun(repo, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
    }

    public string Run(string folder, params string[] arguments)
    {
        var result = TryRun(folder, arguments);
        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    private static GitResult TryRun(string folder, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Commits need an identity; the machine's own git config may have none (CI).
        foreach (var argument in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.com", .. arguments])
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GitResult(process.ExitCode, output, error);
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
