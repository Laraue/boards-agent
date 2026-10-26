using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Updates;

/// <summary>
/// Gets laraue-boards-agent's releases from GitHub, as the install scripts do: the newest one (pre-releases included -
/// GitHub's "latest" leaves them out), downloaded for this system and checked against the release's SHA256SUMS.
/// </summary>
/// <param name="apiUrl">The repository in GitHub's API.</param>
/// <param name="downloadUrl">Where a release's files are, with {tag} for the release.</param>
public sealed class Updater(
    HttpClient http,
    string apiUrl = "https://api.github.com/repos/Laraue/boards-agent",
    string downloadUrl = "https://github.com/Laraue/boards-agent/releases/download/{tag}")
{
    public const string ExecutableName = "laraue-boards-agent";

    /// <summary>The page of a release, for people.</summary>
    public static string ReleasePage(string tag)
    {
        return $"https://github.com/Laraue/boards-agent/releases/tag/{tag}";
    }

    /// <summary>The newest release's tag, e.g. v0.5.0-beta.3.</summary>
    public async Task<string> NewestVersion(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}/releases?per_page=1");
        // GitHub's API refuses requests without a User-Agent.
        request.Headers.UserAgent.ParseAdd($"{ExecutableName}/{RunnerVersion.Value}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var releases = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return releases.RootElement.EnumerateArray().FirstOrDefault().String("tag_name")
            ?? throw new InvalidOperationException("GitHub listed no release of laraue-boards-agent.");
    }

    /// <summary>
    /// Downloads release <paramref name="tag"/> for <paramref name="runtime"/> (e.g. osx-arm64) into
    /// <paramref name="folder"/>, checks it against the release's SHA256SUMS and unpacks it. Returns the new executable.
    /// </summary>
    public async Task<string> Download(string tag, string runtime, string folder, CancellationToken ct)
    {
        var archive = $"{ExecutableName}-{tag}-{runtime}{(runtime.StartsWith("win", StringComparison.Ordinal) ? ".zip" : ".tar.gz")}";
        var baseUrl = downloadUrl.Replace("{tag}", tag);
        var bytes = await http.GetByteArrayAsync($"{baseUrl}/{archive}", ct);
        var sums = await http.GetStringAsync($"{baseUrl}/SHA256SUMS", ct);

        var expected = sums
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == archive)?[0];
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The checksum of {archive} doesn't match the release's SHA256SUMS - not installing it.");
        }

        Directory.CreateDirectory(folder);
        using var stream = new MemoryStream(bytes);
        if (archive.EndsWith(".zip", StringComparison.Ordinal))
        {
            ZipFile.ExtractToDirectory(stream, folder, overwriteFiles: true);
        }
        else
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, folder, overwriteFiles: true);
        }

        var executable = Path.Combine(folder, ExecutableName, OperatingSystem.IsWindows() ? $"{ExecutableName}.exe" : ExecutableName);
        return File.Exists(executable)
            ? executable
            : throw new InvalidOperationException($"{archive} has no {Path.GetFileName(executable)}.");
    }

    /// <summary>
    /// Puts <paramref name="newExecutable"/> in place of <paramref name="target"/>. The new file is copied next to it and
    /// renamed over it, so the target is never half written; a process running the old one keeps it until it restarts.
    /// On Windows the running executable can't be replaced, only renamed: it's moved aside first.
    /// </summary>
    public static void Install(string newExecutable, string target)
    {
        var folder = Path.GetDirectoryName(target)!;
        var staged = Path.Combine(folder, $".{Path.GetFileName(target)}.new");
        File.Copy(newExecutable, staged, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                staged,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        else if (File.Exists(target))
        {
            var old = Path.Combine(folder, $".{Path.GetFileName(target)}.old");
            File.Delete(old);
            File.Move(target, old);
        }

        File.Move(staged, target, overwrite: true);
    }

    /// <summary>This system's release name: osx-arm64, linux-x64, win-x64...</summary>
    public static string CurrentRuntime()
    {
        var os = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return $"{os}-{arch}";
    }
}
