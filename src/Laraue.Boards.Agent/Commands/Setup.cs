using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Boards;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Commands;

/// <summary>
/// `lba setup`: picks the coding agent, signs this machine in to Boards (the key goes to the credential store),
/// maps spaces to local repositories, and makes sure the agent is signed in. Writes the config; running it again keeps
/// what's there.
/// </summary>
static class Setup
{
    public static async Task<int> RunAsync(RunnerArgs options, CancellationToken ct)
    {
        var config = RunnerConfig.TryLoad(options.ConfigPath) ?? new RunnerConfig();
        config = config with
        {
            WebUrl = options.WebUrl ?? config.WebUrl,
            ApiUrl = options.ApiUrl ?? config.ApiUrl,
            McpUrl = options.McpUrl ?? config.McpUrl,
        };

        Console.WriteLine($"laraue-boards-agent {RunnerVersion.Value} setup");

        // 1. Which coding agent works on the issues - before signing in, since the key is named after it.
        var codingAgent = ChooseAgent(config);
        if (codingAgent is null)
        {
            Console.WriteLine("No coding agent is installed. Install Claude Code (https://claude.com/claude-code) or Codex "
                + "(https://developers.openai.com/codex/cli), then run `lba setup` again.");
            return 1;
        }

        config = config with { Agent = codingAgent.Key };

        // 2. Boards: reuse a stored key that still works, unless the person wants another organization.
        var apiKey = ApiKeySource.Resolve(config);
        MeInfo? me = apiKey is null ? null : await TryGetMe(config.McpUrl, apiKey, ct);
        if (me is not null && !Confirm($"Signed in to Boards as {me.User.DisplayName}, organization {me.Organization.Name}. Keep it?", defaultYes: true))
        {
            me = null;
        }

        if (me is null)
        {
            // Only the key this machine stored itself, never BOARDS_AGENT_API_KEY, which may be a key made for something else.
            var storedKey = config.OrganizationId is { } previousOrganizationId
                ? CredentialStore.Load(CredentialStore.Account(previousOrganizationId))
                : null;
            var signedIn = await BoardsSignIn.RunAsync(config, codingAgent.ClientName, !options.NoBrowser, storedKey, ct);
            if (signedIn is null)
            {
                return 1;
            }

            CredentialStore.Save(
                CredentialStore.Account(signedIn.OrganizationId),
                $"Boards agent - {signedIn.OrganizationName}",
                signedIn.ApiKey);
            apiKey = signedIn.ApiKey;
            me = await TryGetMe(config.McpUrl, apiKey, ct)
                ?? throw new InvalidOperationException($"The new API key doesn't work with {config.McpUrl}.");

            // Spaces belong to an organization: another organization starts over.
            if (config.OrganizationId != signedIn.OrganizationId)
            {
                config = config with { Spaces = [] };
            }

            config = config with { OrganizationId = signedIn.OrganizationId, OrganizationName = signedIn.OrganizationName };
            Console.WriteLine($"Signed in as {me.User.DisplayName}, organization {me.Organization.Name}. The API key is in your {CredentialStoreName}.");
        }

        // 3. Where the code is: a space's repositories are the clones its links point to, found by git remote.
        config = config with { CodeFolders = [AskCodeFolder(config, canPick: !options.NoBrowser && FolderPicker.IsSupported)] };
        var anyRepository = false;
        await using (var boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey!, ct))
        {
            var resolver = new SpaceResolver(boards, config, new CloneIndex(config.CodeFolders));
            var spaces = await boards.ListSpaces(ct);
            Console.WriteLine();
            Console.WriteLine($"Your spaces' repositories under {config.CodeFolders[0]}:");
            foreach (var space in spaces)
            {
                var context = await resolver.Resolve(space.Key, ct);
                anyRepository |= context.Repos.Count > 0;
                Console.WriteLine(Report(space, context));
            }

            if (!anyRepository)
            {
                Console.WriteLine();
                Console.WriteLine("No space links to a repository cloned there yet. Add the repository's URL as a link of the space in "
                    + "Boards (space settings) - the agent picks it up by itself - or map one by hand now.");
                config = config with { Spaces = MapSpaces(spaces, config.Spaces) };
                anyRepository = config.Spaces.Values.Any(x => x.Repo is not null);
            }
        }

        // 4. Where the agent works: separate worktrees, or your own clones.
        config = config with { Workspace = ChooseWorkspace(config) };

        // 5. What happens to the agent's work: kept local, pushed, or a pull request.
        config = config with { Publish = ChoosePublish(config) };

        // 6. Whose pull request review comments a re-run gets.
        config = config with { ReviewComments = ChooseReviewComments(config) };

        // 7. Claude Code only: may it use your Chrome when you continue a session with it?
        if (codingAgent.Key == CodingAgents.Claude)
        {
            Console.WriteLine();
            config = config with
            {
                Chrome = Confirm(
                    "When you continue an agent's session (lba continue), may Claude Code use your Chrome (the Claude in Chrome extension) to open the app and check its work? You approve each browser action; background runs never use the browser.",
                    defaultYes: config.Chrome),
            };
        }

        config.Save(options.ConfigPath);
        Console.WriteLine($"Saved {options.ConfigPath}.");

        // 8. The coding agent's own sign-in.
        string executable;
        try
        {
            executable = codingAgent.FindExecutable();
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine($"{codingAgent.Name} isn't installed. Install it ({codingAgent.InstallUrl}), then run `lba setup` again.");
            return 1;
        }

        if (!codingAgent.IsSignedIn(executable))
        {
            Console.WriteLine($"{codingAgent.Name} isn't signed in. Signing in...");
            codingAgent.SignIn(executable);
            if (!codingAgent.IsSignedIn(executable))
            {
                Console.WriteLine($"{codingAgent.Name} still isn't signed in. Run `lba setup` again once it is.");
                return 1;
            }
        }

        Console.WriteLine($"{codingAgent.Name}: {executable} (signed in).");
        Console.WriteLine();
        Console.WriteLine(anyRepository
            ? "Done. The agent takes the issues you hand to it in the spaces above that have a repository here."
            : "No space has a repository on this machine yet, so there's nothing to work on. Add your repositories as links of the space in Boards, then run `lba setup` again.");
        if (anyRepository && Service.IsSupported)
        {
            if (Service.IsInstalled)
            {
                // The running service read the old config at start. Restarting waits for it to stop, which takes a moment.
                Console.WriteLine("Restarting the background service...");
                Service.Restart();
                Console.WriteLine("The background service was restarted with the new settings. Hand an issue to the agent in Boards.");
            }
            else if (Confirm("Run the agent in the background, also after you restart the computer?", defaultYes: true))
            {
                return Service.Install(options.ConfigPath);
            }
            else
            {
                Console.WriteLine("Start the runner with `lba`, then hand an issue to the agent in Boards.");
            }
        }
        else if (anyRepository)
        {
            Console.WriteLine("Start the runner with `lba`, then hand an issue to the agent in Boards.");
        }

        return 0;
    }

    /// <summary>
    /// The installed agent to use: the only one installed, or the person's pick when several are (the config's current
    /// one by default). Null when none is installed.
    /// </summary>
    private static ICodingAgent? ChooseAgent(RunnerConfig config)
    {
        var installed = CodingAgents.All(config)
            .Where(IsInstalled)
            .ToList();
        if (installed.Count <= 1)
        {
            if (installed.Count == 1)
            {
                Console.WriteLine($"Coding agent: {installed[0].Name}.");
            }

            return installed.FirstOrDefault();
        }

        var current = installed.FirstOrDefault(x => x.Key == config.Agent.ToLowerInvariant()) ?? installed[0];
        Console.WriteLine();
        Console.WriteLine("Which agent should work on the issues you hand off?");
        for (var i = 0; i < installed.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {installed[i].Name}");
        }

        while (true)
        {
            var answer = Ask($"Number, or Enter for {current.Name}:");
            if (answer.Length == 0)
            {
                return current;
            }

            if (int.TryParse(answer, out var number) && number >= 1 && number <= installed.Count)
            {
                return installed[number - 1];
            }

            Console.WriteLine($"Enter a number from 1 to {installed.Count}.");
        }
    }

    /// <summary>The workspace mode (<see cref="RunnerConfig.Workspace"/>), the config's current one by default.</summary>
    /// <summary>The publish mode (<see cref="RunnerConfig.Publish"/>), the config's current one by default.</summary>
    private static string ChoosePublish(RunnerConfig config)
    {
        string[] modes = [PublishModes.Off, PublishModes.Push, PublishModes.PullRequest];
        var current = Array.IndexOf(modes, config.Publish.ToLowerInvariant()) + 1;
        if (current == 0)
        {
            current = 1;
        }

        Console.WriteLine();
        Console.WriteLine("When the agent is done, what should happen to its branch?");
        Console.WriteLine("  1. Nothing - it stays on this computer, you review it and push it yourself.");
        Console.WriteLine("  2. Push it to the repository's remote.");
        Console.WriteLine("  3. Push it and open a pull request (needs the GitHub CLI, gh, signed in).");
        Console.WriteLine("  The agent itself never pushes: laraue-boards-agent does, after the run is done.");
        while (true)
        {
            var answer = Ask($"Number, or Enter for {current}:");
            var number = answer.Length == 0 ? current : int.TryParse(answer, out var parsed) ? parsed : 0;
            if (number is >= 1 and <= 3)
            {
                if (modes[number - 1] == PublishModes.PullRequest && CodingAgents.FindOnPath("gh") is null)
                {
                    Console.WriteLine("The GitHub CLI (gh) isn't installed: install it (https://cli.github.com) and run `gh auth login`, or the branch is only pushed.");
                }

                return modes[number - 1];
            }

            Console.WriteLine("Enter 1, 2 or 3.");
        }
    }

    /// <summary>The global review comments level (<see cref="RunnerConfig.ReviewComments"/>), the config's current one by default.</summary>
    private static string ChooseReviewComments(RunnerConfig config)
    {
        string[] levels = [ReviewCommentLevels.Off, ReviewCommentLevels.Owner, ReviewCommentLevels.Collaborators, ReviewCommentLevels.Anyone];
        var current = Array.IndexOf(levels, config.ReviewComments.ToLowerInvariant()) + 1;
        if (current == 0)
        {
            current = 3;
        }

        Console.WriteLine();
        Console.WriteLine("When an issue goes back to the agent, it gets the open review comments of the branch's pull request.");
        Console.WriteLine("Whose comments? The agent runs builds and tests on this computer, so only people you trust should steer it.");
        Console.WriteLine("  1. Nobody's - don't read pull requests.");
        Console.WriteLine("  2. Only the repository owner's - for personal repositories; on an organization's, GitHub rarely marks anyone as owner.");
        Console.WriteLine("  3. The owner's, organization members' and collaborators' - people with access to the repository (recommended).");
        Console.WriteLine("  4. Anyone's - on a public repository that's anyone with a GitHub account; only for private repositories.");
        Console.WriteLine("  For one repository, run `lba review-comments <level>` in its clone later.");
        while (true)
        {
            var answer = Ask($"Number, or Enter for {current}:");
            var number = answer.Length == 0 ? current : int.TryParse(answer, out var parsed) ? parsed : 0;
            if (number is >= 1 and <= 4)
            {
                return levels[number - 1];
            }

            Console.WriteLine("Enter 1, 2, 3 or 4.");
        }
    }

    private static string ChooseWorkspace(RunnerConfig config)
    {
        string[] modes = [WorkspaceModes.Worktree, WorkspaceModes.Checkout];
        var current = config.InCheckouts ? 2 : 1;
        Console.WriteLine();
        Console.WriteLine("Where should the agent work?");
        Console.WriteLine($"  1. In a separate git worktree per issue under {config.WorktreeFolder} - your clones aren't touched.");
        Console.WriteLine("  2. In your own clones, one issue at a time: each switched to a new branch from the fetched default branch,");
        Console.WriteLine("     so you review and run the result where you always work. Before switching a clone that has uncommitted");
        Console.WriteLine("     changes or is on another branch, the agent asks you first.");
        while (true)
        {
            var answer = Ask($"Number, or Enter for {current}:");
            if (answer.Length == 0)
            {
                return modes[current - 1];
            }

            if (int.TryParse(answer, out var number) && number is 1 or 2)
            {
                return modes[number - 1];
            }

            Console.WriteLine("Enter 1 or 2.");
        }
    }

    private static bool IsInstalled(ICodingAgent agent)
    {
        try
        {
            return File.Exists(agent.FindExecutable());
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static string CredentialStoreName => OperatingSystem.IsMacOS() ? "Keychain" : "credential store";

    /// <summary>The user the key signs in as, or null when it doesn't work (revoked, or for another Boards).</summary>
    private static async Task<MeInfo?> TryGetMe(string mcpUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            await using var boards = await BoardsClient.ConnectAsync(mcpUrl, apiKey, ct);
            return await boards.GetMe(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folder the code is cloned under, typed or - on a desktop - picked in Finder/Explorer. Nothing is guessed:
    /// Enter keeps the folder configured before, or opens the picker when there's none.
    /// </summary>
    private static string AskCodeFolder(RunnerConfig config, bool canPick)
    {
        var current = config.CodeFolders.FirstOrDefault();
        var picker = OperatingSystem.IsWindows() ? "Explorer" : "Finder";
        var question = (current, canPick) switch
        {
            (null, true) => $"Which folder are your repositories cloned under? Press Enter to choose it in {picker}, or type a path:",
            (null, false) => "Which folder are your repositories cloned under (e.g. ~/Repos)? Type a path:",
            (_, true) => $"Which folder are your repositories cloned under? Enter for {current}, f to choose in {picker}, or type a path:",
            _ => $"Which folder are your repositories cloned under? Enter for {current}, or type a path:",
        };
        Console.WriteLine("(They're searched three levels deep.)");

        while (true)
        {
            var answer = Ask(question);
            string? folder;
            if (answer.Length == 0 && current is not null)
            {
                folder = current;
            }
            else if (canPick && (answer.Length == 0 || answer is "f" or "F"))
            {
                folder = FolderPicker.Pick("Choose the folder your repositories are cloned under");
                if (folder is null)
                {
                    if (Console.IsInputRedirected && Console.In.Peek() < 0)
                    {
                        throw new InvalidOperationException("No folder for the repositories was given.");
                    }

                    continue;
                }

                Console.WriteLine(folder);
            }
            else if (answer.Length == 0)
            {
                if (Console.IsInputRedirected && Console.In.Peek() < 0)
                {
                    throw new InvalidOperationException("No folder for the repositories was given.");
                }

                continue;
            }
            else
            {
                folder = answer;
            }

            folder = Paths.Expand(folder);
            if (Directory.Exists(folder))
            {
                return folder;
            }

            Console.WriteLine($"{folder} doesn't exist.");
        }
    }

    /// <summary>One line per space: its repositories here, or why it has none.</summary>
    private static string Report(SpaceSummary space, SpaceContext context)
    {
        var notCloned = context.OtherLinks
            .Select(x => RepoUrl.Normalize(x.Url))
            .OfType<string>()
            .Where(LooksLikeRepository)
            .ToList();
        var line = context.Repos.Count > 0
            ? $"  {space.Key} {space.Name}: {string.Join(", ", context.Repos.Select(x => x.Name))}"
            : $"  {space.Key} {space.Name}: {(notCloned.Count > 0 ? "no repository cloned here" : "no repository links")}";
        return notCloned.Count > 0 ? $"{line} (not cloned here: {string.Join(", ", notCloned)})" : line;
    }

    /// <summary>A link to a repository host's repository page, rather than to docs or a design.</summary>
    private static bool LooksLikeRepository(string normalizedUrl)
    {
        var parts = normalizedUrl.Split('/');
        return parts.Length == 3 && parts[0] is "github.com" or "gitlab.com" or "bitbucket.org" or "codeberg.org";
    }

    private static Dictionary<string, SpaceConfig> MapSpaces(IReadOnlyList<SpaceSummary> spaces, Dictionary<string, SpaceConfig> existing)
    {
        var mapped = existing
            .Where(x => spaces.Any(space => space.Key == x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        if (spaces.Count == 0)
        {
            Console.WriteLine("You can't read any space in this organization yet.");
            return mapped;
        }

        Console.WriteLine();
        Console.WriteLine("Spaces:");
        for (var i = 0; i < spaces.Count; i++)
        {
            var current = mapped.TryGetValue(spaces[i].Key, out var space) ? $" -> {space.Repo}" : "";
            Console.WriteLine($"  {i + 1}. {spaces[i].Key} {spaces[i].Name}{current}");
        }

        var here = Git.MainCheckout(Environment.CurrentDirectory);
        if (here is not null && !mapped.Values.Any(x => x.Repo == here))
        {
            var space = AskSpace(spaces, $"Which space's issues should be worked on in {here}? Number, or Enter to skip:");
            if (space is not null)
            {
                mapped[space.Key] = new SpaceConfig { Repo = here, BaseRef = Git.DefaultBaseRef(here) };
            }
        }

        while (AskSpace(spaces, mapped.Count == 0 ? "Map a space to a repository? Number, or Enter to finish:" : "Map another space? Number, or Enter to finish:") is { } space)
        {
            var folder = Ask($"Folder of the {space.Key} repository (a git checkout):");
            var repo = folder.Length == 0 ? null : Git.FindCheckout(Paths.Expand(folder));
            if (repo is null)
            {
                Console.WriteLine($"{folder} isn't a git checkout.");
                continue;
            }

            mapped[space.Key] = new SpaceConfig { Repo = repo, BaseRef = Git.DefaultBaseRef(repo) };
        }

        foreach (var (key, space) in mapped)
        {
            Console.WriteLine($"  {key} -> {space.Repo} (branches from {space.BaseRef})");
        }

        return mapped;
    }

    private static SpaceSummary? AskSpace(IReadOnlyList<SpaceSummary> spaces, string question)
    {
        while (true)
        {
            var answer = Ask(question);
            if (answer.Length == 0)
            {
                return null;
            }

            if (int.TryParse(answer, out var number) && number >= 1 && number <= spaces.Count)
            {
                return spaces[number - 1];
            }

            var byKey = spaces.FirstOrDefault(x => string.Equals(x.Key, answer, StringComparison.OrdinalIgnoreCase));
            if (byKey is not null)
            {
                return byKey;
            }

            Console.WriteLine($"Enter a number from 1 to {spaces.Count}.");
        }
    }

    public static bool Confirm(string question, bool defaultYes)
    {
        var answer = Ask($"{question} [{(defaultYes ? "Y/n" : "y/N")}]");
        return answer.Length == 0 ? defaultYes : answer.StartsWith('y') || answer.StartsWith('Y');
    }

    private static string Ask(string question)
    {
        Console.Write($"{question} ");
        // End of input (e.g. nothing piped in) counts as Enter.
        return Console.ReadLine()?.Trim() ?? "";
    }
}
