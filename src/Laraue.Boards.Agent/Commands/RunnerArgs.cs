using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;

namespace Laraue.Boards.Agent.Commands;

/// <summary>
/// Command-line options. <see cref="WebUrl"/>, <see cref="ApiUrl"/> and <see cref="McpUrl"/> point setup at another
/// Boards than production, e.g. a local one.
/// </summary>
public sealed record RunnerArgs(
    string? Command,
    string ConfigPath,
    bool Once,
    bool DryRun,
    bool Check,
    bool Version,
    string? WebUrl,
    string? ApiUrl,
    string? McpUrl,
    bool NoBrowser,
    string? ServiceAction,
    string? LogPath,
    string? IssueKey = null,
    string? Instruction = null,
    IReadOnlyList<string>? ReviewCommentsArguments = null,
    bool ReviewCommentsDefault = false)
{
    public const string SetupCommand = "setup";

    public const string ContinueCommand = "continue";

    public const string ServiceCommand = "service";

    public const string ReviewCommentsCommand = "review-comments";

    private const string Usage =
        "Usage: lba [--config <file>] [--log <file>] [--once] [--dry-run] [--check] [--version]   (or laraue-boards-agent)\n" +
        "       lba setup [--no-browser] [--web-url <url>] [--api-url <url>] [--mcp-url <url>]\n" +
        "       lba continue [issue key] [instruction]\n" +
        "       lba review-comments [[repository] off|owner|collaborators|anyone|default] [--default <level>]\n" +
        "       lba service install|uninstall|restart|status [--config <file>]\n" +
        "       lba status   (the same as lba service status)";

    public static RunnerArgs Parse(string[] args)
    {
        string? command = null;
        var configPath = RunnerConfig.DefaultPath;
        var once = false;
        var dryRun = false;
        var check = false;
        var version = false;
        string? webUrl = null;
        string? apiUrl = null;
        string? mcpUrl = null;
        var noBrowser = false;
        string? serviceAction = null;
        string? logPath = null;
        string? issueKey = null;
        string? instruction = null;
        var reviewArguments = new List<string>();
        var reviewDefault = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case SetupCommand when i == 0:
                    command = SetupCommand;
                    break;
                case ContinueCommand when i == 0:
                    command = ContinueCommand;

                    // The issue key is optional (ContinueCommand picks the issue then); the instruction is every word up
                    // to the first option, quoted or not: lba continue MIN-21 print the json for grafana
                    if (i + 1 < args.Length && IssueKeys.IsKey(args[i + 1]))
                    {
                        issueKey = args[++i].ToUpperInvariant();
                    }

                    var words = new List<string>();
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        words.Add(args[++i]);
                    }

                    instruction = words.Count == 0 ? null : string.Join(' ', words);

                    break;
                case ReviewCommentsCommand when i == 0:
                    command = ReviewCommentsCommand;
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        reviewArguments.Add(args[++i]);
                    }

                    break;
                case "--default" when command == ReviewCommentsCommand && i + 1 < args.Length:
                    reviewDefault = true;
                    reviewArguments.Add(args[++i]);
                    break;
                // `lba status`: what people type to see whether the runner works - the same as `lba service status`.
                case "status" when i == 0:
                    command = ServiceCommand;
                    serviceAction = "status";
                    break;
                case ServiceCommand when i == 0 && i + 1 < args.Length:
                    command = ServiceCommand;
                    serviceAction = args[++i];
                    break;
                case "--config" when i + 1 < args.Length:
                    configPath = Paths.Expand(args[++i]);
                    break;
                case "--web-url" when i + 1 < args.Length:
                    webUrl = args[++i];
                    break;
                case "--api-url" when i + 1 < args.Length:
                    apiUrl = args[++i];
                    break;
                case "--mcp-url" when i + 1 < args.Length:
                    mcpUrl = args[++i];
                    break;
                case "--log" when i + 1 < args.Length:
                    logPath = Paths.Expand(args[++i]);
                    break;
                case "--no-browser":
                    noBrowser = true;
                    break;
                case "--once":
                    once = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--check":
                    check = true;
                    break;
                case "--version":
                    version = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}.\n{Usage}");
            }
        }

        if (command == ServiceCommand && serviceAction is not ("install" or "uninstall" or "restart" or "status"))
        {
            throw new ArgumentException($"Unknown service action: {serviceAction}.\n{Usage}");
        }

        if (command == ReviewCommentsCommand && (reviewArguments.Count > 2 || (reviewDefault && reviewArguments.Count != 1)))
        {
            throw new ArgumentException($"Too many arguments for review-comments.\n{Usage}");
        }

        return new RunnerArgs(
            command,
            configPath,
            once,
            dryRun,
            check,
            version,
            webUrl,
            apiUrl,
            mcpUrl,
            noBrowser,
            serviceAction,
            logPath,
            issueKey,
            instruction,
            reviewArguments,
            reviewDefault);
    }
}
