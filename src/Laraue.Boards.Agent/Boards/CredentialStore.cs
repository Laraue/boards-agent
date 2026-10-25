using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Boards;

/// <summary>
/// The API key in the OS credential store: the macOS Keychain (security), the Secret Service on Linux (secret-tool), or
/// the Windows Credential Manager.
/// One entry per organization. The key never goes on a command line - it's passed on stdin - and never to a file.
/// </summary>
static class CredentialStore
{
    private const string Service = "boards-agent";

    public static string Account(long organizationId)
    {
        return $"organization-{organizationId}";
    }

    public static void Save(string account, string label, string secret)
    {
        ProcessResult result;
        if (OperatingSystem.IsMacOS())
        {
            // security's interactive mode reads the command from stdin, so the key isn't visible in the process list.
            // -A lets the runner read it back after an upgrade changes its binary, without a Keychain prompt.
            var safeLabel = label.Replace("\"", "").Replace("\\", "");
            result = Shell.Run(
                new System.Diagnostics.ProcessStartInfo("/usr/bin/security", "-i"),
                $"add-generic-password -U -A -s {Service} -a {account} -l \"{safeLabel}\" -w {secret}\n");
        }
        else if (OperatingSystem.IsLinux())
        {
            var info = new System.Diagnostics.ProcessStartInfo("secret-tool");
            foreach (var argument in new[] { "store", $"--label={label}", "service", Service, "account", account })
            {
                info.ArgumentList.Add(argument);
            }

            result = Shell.Run(info, secret);
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsCredentials.Write($"{Service}:{account}", label, secret);
            return;
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Storing the key isn't supported on this system yet. Set the BOARDS_AGENT_API_KEY environment variable instead.");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Couldn't save the API key in the credential store: {result.Error.Trim()}");
        }
    }

    /// <summary>The stored key, or null when there's none (or no credential store).</summary>
    public static string? Load(string account)
    {
        ProcessResult result;
        if (OperatingSystem.IsMacOS())
        {
            result = Shell.Run("/usr/bin/security", "find-generic-password", "-s", Service, "-a", account, "-w");
        }
        else if (OperatingSystem.IsLinux())
        {
            result = Shell.Run("secret-tool", "lookup", "service", Service, "account", account);
        }
        else if (OperatingSystem.IsWindows())
        {
            return WindowsCredentials.Read($"{Service}:{account}");
        }
        else
        {
            return null;
        }

        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } secret ? secret : null;
    }
}
