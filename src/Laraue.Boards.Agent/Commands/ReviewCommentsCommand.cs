using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Publishing;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Commands;

/// <summary>
/// `lba review-comments`: whose pull request review comments re-runs get (<see cref="ReviewCommentLevels"/>), globally
/// and per repository. Kept in this computer's config, not in Boards: it protects the computer the agent runs on, so
/// nobody else should be able to raise it.
/// </summary>
static class ReviewCommentsCommand
{
    public const string ResetLevel = "default";

    public static async Task<int> RunAsync(RunnerArgs options, CancellationToken ct)
    {
        var config = RunnerConfig.Load(options.ConfigPath);
        var arguments = options.ReviewCommentsArguments ?? [];
        if (options.ReviewCommentsDefault)
        {
            var level = Level(arguments[0], allowReset: false);
            (config with { ReviewComments = level }).Save(options.ConfigPath);
            Console.WriteLine($"Default: {ReviewCommentLevels.Describe(level)}. Repositories with their own level keep it.");
            return 0;
        }

        switch (arguments.Count)
        {
            case 0:
                return await List(config, ct);
            case 1 when !IsLevel(arguments[0]):
                throw new ArgumentException($"Give a level for {arguments[0]}: {string.Join(", ", ReviewCommentLevels.All)} or {ResetLevel}.");
        }

        var repository = arguments.Count == 2 ? Repository(arguments[0]) : CurrentRepository();
        var newLevel = Level(arguments[^1], allowReset: true);
        if (newLevel == ReviewCommentLevels.Anyone
            && Visibility(repository) != "PRIVATE"
            && !ConsolePrompt.Confirm($"{repository} isn't known to be private: anyone with a GitHub account could steer the agent on this computer with a comment. Trust anyone's comments anyway?", defaultYes: false))
        {
            return 1;
        }

        var levels = new Dictionary<string, string>(config.RepositoryReviewComments);
        if (newLevel == ResetLevel)
        {
            levels.Remove(repository);
        }
        else
        {
            levels[repository] = newLevel;
        }

        (config with { RepositoryReviewComments = levels }).Save(options.ConfigPath);
        Console.WriteLine(newLevel == ResetLevel
            ? $"{repository}: the default, {ReviewCommentLevels.Describe(config.ReviewComments)}."
            : $"{repository}: {ReviewCommentLevels.Describe(newLevel)}.");
        return 0;
    }

    /// <summary>The repositories of the person's spaces cloned here, and any with their own level, with their levels.</summary>
    private static async Task<int> List(RunnerConfig config, CancellationToken ct)
    {
        var repositories = new SortedSet<string>(config.RepositoryReviewComments.Keys);
        if (ApiKeySource.Resolve(config) is { } apiKey)
        {
            try
            {
                await using var boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey, ct);
                var resolver = new SpaceResolver(boards, config, new CloneIndex(config.CodeFolders));
                foreach (var space in await boards.ListSpaces(ct))
                {
                    var context = await resolver.Resolve(space.Key, ct);
                    repositories.UnionWith(context.Repos
                        .Select(x => x.Url is null ? Git.RemoteUrls(x.ClonePath).FirstOrDefault() : RepoUrl.Normalize(x.Url))
                        .OfType<string>());
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"Couldn't read your spaces from Boards ({e.Message}) - only repositories with their own level are listed.");
            }
        }
        else
        {
            Console.WriteLine("No Boards API key (run `lba setup`) - only repositories with their own level are listed.");
        }

        Console.WriteLine($"Pull request review comments re-runs get - default: {config.ReviewComments}.");
        var width = repositories.Count == 0 ? 0 : repositories.Max(x => x.Length);
        foreach (var repository in repositories)
        {
            var visibility = Visibility(repository)?.ToLowerInvariant() ?? "?";
            var level = config.RepositoryReviewComments.TryGetValue(repository, out var own) ? own : $"{config.ReviewComments} (default)";
            Console.WriteLine($"  {repository.PadRight(width)}  {visibility,-8}  {level}");
        }

        if (repositories.Count == 0)
        {
            Console.WriteLine("  (no repositories yet)");
        }

        Console.WriteLine("Change one: `lba review-comments <level>` in its clone, or `lba review-comments <owner/name> <level>`.");
        Console.WriteLine($"Levels: {string.Join(", ", ReviewCommentLevels.All)}; `{ResetLevel}` removes a repository's own level.");
        return 0;
    }

    private static bool IsLevel(string value)
    {
        return ReviewCommentLevels.All.Contains(value, StringComparer.OrdinalIgnoreCase)
            || value.Equals(ResetLevel, StringComparison.OrdinalIgnoreCase);
    }

    private static string Level(string value, bool allowReset)
    {
        if (!IsLevel(value) || (!allowReset && value.Equals(ResetLevel, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown level: {value}. Use {string.Join(", ", ReviewCommentLevels.All)}{(allowReset ? $" or {ResetLevel}" : "")}.");
        }

        return value.ToLowerInvariant();
    }

    /// <summary>A repository as the config keys it: owner/name (GitHub), host/owner/name or any remote URL.</summary>
    private static string Repository(string value)
    {
        var normalized = RepoUrl.Normalize(value)
            ?? throw new ArgumentException($"Not a repository: {value}. Use owner/name, e.g. acme/api.");
        return normalized.Count(x => x == '/') == 1 ? $"github.com/{normalized}" : normalized;
    }

    /// <summary>The repository of the clone the command runs in (its first remote; origin when it has one).</summary>
    private static string CurrentRepository()
    {
        var checkout = Git.MainCheckout(Environment.CurrentDirectory)
            ?? throw new ArgumentException("Not in a git clone: run it in the repository's clone, or name it - lba review-comments <owner/name> <level>.");
        var origin = Git.OriginUrl(checkout) is { } url ? RepoUrl.Normalize(url) : null;
        return origin ?? Git.RemoteUrls(checkout).FirstOrDefault()
            ?? throw new ArgumentException($"{checkout} has no remote: name the repository - lba review-comments <owner/name> <level>.");
    }

    /// <summary>PUBLIC, PRIVATE or INTERNAL from GitHub (via gh); null when that can't be told.</summary>
    private static string? Visibility(string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 3 || parts[0] != "github.com" || CodingAgents.FindOnPath("gh") is not { } gh)
        {
            return null;
        }

        var result = Publisher.RunGh(gh, Environment.CurrentDirectory, ["repo", "view", $"{parts[1]}/{parts[2]}", "--json", "visibility", "--jq", ".visibility"]);
        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } visibility ? visibility : null;
    }
}
