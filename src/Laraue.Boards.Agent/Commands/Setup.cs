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
public static class Setup
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

        // 2. Boards.
        if (await SignInToBoards(config, codingAgent, options, ct) is not { } access)
        {
            return 1;
        }

        // 3. Where the code is.
        var repositories = await FindRepositories(access.Config, access.ApiKey, options, ct);

        // 4.-7. How the agent works.
        config = AskHowTheAgentWorks(repositories.Config, codingAgent);
        config.Save(options.ConfigPath);
        Console.WriteLine($"Saved {options.ConfigPath}.");

        // 8. The coding agent's own sign-in.
        if (EnsureAgentSignedIn(codingAgent) is not { } executable)
        {
            return 1;
        }

        Console.WriteLine($"{codingAgent.Name}: {executable} (signed in).");
        return OfferBackgroundService(options, repositories.Any);
    }

    private sealed record BoardsAccess(RunnerConfig Config, string ApiKey);

    /// <summary>
    /// Reuses a stored key that still works, unless the person wants another organization; else signs in through the
    /// browser and stores the new key. Null when the sign-in didn't complete.
    /// </summary>
    private static async Task<BoardsAccess?> SignInToBoards(RunnerConfig config, ICodingAgent codingAgent, RunnerArgs options, CancellationToken ct)
    {
        var apiKey = ApiKeySource.Resolve(config);
        var me = apiKey is null ? null : await TryGetMe(config.McpUrl, apiKey, ct);
        if (me is not null && ConsolePrompt.Confirm($"Signed in to Boards as {me.User.DisplayName}, organization {me.Organization.Name}. Keep it?", defaultYes: true))
        {
            return new BoardsAccess(config, apiKey!);
        }

        // Only the key this machine stored itself, never BOARDS_AGENT_API_KEY, which may be a key made for something else.
        var storedKey = config.OrganizationId is { } previousOrganizationId
            ? CredentialStore.Load(CredentialStore.Account(previousOrganizationId))
            : null;
        var signedIn = await BoardsSignIn.RunAsync(config, codingAgent.ClientName, !options.NoBrowser, storedKey, ct);
        if (signedIn is null)
        {
            return null;
        }

        CredentialStore.Save(
            CredentialStore.Account(signedIn.OrganizationId),
            $"Boards agent - {signedIn.OrganizationName}",
            signedIn.ApiKey);
        me = await TryGetMe(config.McpUrl, signedIn.ApiKey, ct)
            ?? throw new InvalidOperationException($"The new API key doesn't work with {config.McpUrl}.");

        // Spaces belong to an organization: another organization starts over.
        if (config.OrganizationId != signedIn.OrganizationId)
        {
            config = config with { Spaces = [] };
        }

        config = config with { OrganizationId = signedIn.OrganizationId, OrganizationName = signedIn.OrganizationName };
        Console.WriteLine($"Signed in as {me.User.DisplayName}, organization {me.Organization.Name}. The API key is in your {CredentialStoreName}.");
        return new BoardsAccess(config, signedIn.ApiKey);
    }

    private sealed record Repositories(RunnerConfig Config, bool Any);

    /// <summary>
    /// Asks for the code folder and shows each space's repositories found there: a space's repositories are the clones
    /// its links point to, found by git remote. With none, offers to map one by hand.
    /// </summary>
    private static async Task<Repositories> FindRepositories(RunnerConfig config, string apiKey, RunnerArgs options, CancellationToken ct)
    {
        config = config with { CodeFolders = [AskCodeFolder(config, canPick: !options.NoBrowser && FolderPicker.IsSupported)] };
        var anyRepository = false;
        await using var boards = await BoardsClient.ConnectAsync(config.McpUrl, apiKey, ct);
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

        return new Repositories(config, anyRepository);
    }

    /// <summary>Where the agent works, what happens to its work, whose review comments it gets, and whether it may use Chrome.</summary>
    private static RunnerConfig AskHowTheAgentWorks(RunnerConfig config, ICodingAgent codingAgent)
    {
        config = config with { Workspace = ChooseWorkspace(config) };
        config = config with { Publish = ChoosePublish(config) };
        config = config with { ReviewComments = ChooseReviewComments(config) };

        // Claude Code only: may it use your Chrome when you continue a session with it?
        if (codingAgent.Key == CodingAgents.Claude)
        {
            Console.WriteLine();
            config = config with
            {
                Chrome = ConsolePrompt.Confirm(
                    "When you continue an agent's session (lba continue), may Claude Code use your Chrome (the Claude in Chrome extension) to open the app and check its work? You approve each browser action; background runs never use the browser.",
                    defaultYes: config.Chrome),
            };
        }

        return config;
    }

    /// <summary>The agent's executable once it's signed in (signing it in if needed); null, said why, when it can't be.</summary>
    private static string? EnsureAgentSignedIn(ICodingAgent codingAgent)
    {
        string executable;
        try
        {
            executable = codingAgent.FindExecutable();
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine($"{codingAgent.Name} isn't installed. Install it ({codingAgent.InstallUrl}), then run `lba setup` again.");
            return null;
        }

        if (!codingAgent.IsSignedIn(executable))
        {
            Console.WriteLine($"{codingAgent.Name} isn't signed in. Signing in...");
            codingAgent.SignIn(executable);
            if (!codingAgent.IsSignedIn(executable))
            {
                Console.WriteLine($"{codingAgent.Name} still isn't signed in. Run `lba setup` again once it is.");
                return null;
            }
        }

        return executable;
    }

    /// <summary>What's next: restarts the background service with the new settings, or offers to install it.</summary>
    private static int OfferBackgroundService(RunnerArgs options, bool anyRepository)
    {
        Console.WriteLine();
        if (!anyRepository)
        {
            Console.WriteLine("No space has a repository on this machine yet, so there's nothing to work on. Add your repositories as links of the space in Boards, then run `lba setup` again.");
            return 0;
        }

        Console.WriteLine("Done. The agent takes the issues you hand to it in the spaces above that have a repository here.");
        if (!Service.IsSupported)
        {
            Console.WriteLine("Start the runner with `lba`, then hand an issue to the agent in Boards.");
        }
        else if (Service.IsInstalled)
        {
            // The running service read the old config at start. Restarting waits for it to stop, which takes a moment.
            Console.WriteLine("Restarting the background service...");
            Service.Restart();
            Console.WriteLine("The background service was restarted with the new settings. Hand an issue to the agent in Boards.");
        }
        else if (ConsolePrompt.Confirm("Run the agent in the background, also after you restart the computer?", defaultYes: true))
        {
            return Service.Install(options.ConfigPath);
        }
        else
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
        var chosen = ConsolePrompt.Choose(
            "Which agent should work on the issues you hand off?",
            installed.Select(x => x.Name).ToList(),
            installed.IndexOf(current),
            defaultLabel: current.Name);
        return installed[chosen];
    }

    /// <summary>The publish mode (<see cref="RunnerConfig.Publish"/>), the config's current one by default.</summary>
    private static string ChoosePublish(RunnerConfig config)
    {
        string[] modes = [PublishModes.Off, PublishModes.Push, PublishModes.PullRequest];
        var mode = modes[ConsolePrompt.Choose(
            "When the agent is done, what should happen to its branch?",
            [
                "Nothing - it stays on this computer, you review it and push it yourself.",
                "Push it to the repository's remote.",
                "Push it and open a pull request (needs the GitHub CLI, gh, signed in).",
            ],
            CurrentIndex(modes, config.Publish, fallback: 0),
            note: "The agent itself never pushes: laraue-boards-agent does, after the run is done.")];
        if (mode == PublishModes.PullRequest && CodingAgents.FindOnPath("gh") is null)
        {
            Console.WriteLine("The GitHub CLI (gh) isn't installed: install it (https://cli.github.com) and run `gh auth login`, or the branch is only pushed.");
        }

        return mode;
    }

    /// <summary>The global review comments level (<see cref="RunnerConfig.ReviewComments"/>), the config's current one by default.</summary>
    private static string ChooseReviewComments(RunnerConfig config)
    {
        string[] levels = [ReviewCommentLevels.Off, ReviewCommentLevels.Owner, ReviewCommentLevels.Collaborators, ReviewCommentLevels.Anyone];
        return levels[ConsolePrompt.Choose(
            "When an issue goes back to the agent, it gets the open review comments of the branch's pull request.\n"
                + "Whose comments? The agent runs builds and tests on this computer, so only people you trust should steer it.",
            [
                "Nobody's - don't read pull requests.",
                "Only the repository owner's - for personal repositories; on an organization's, GitHub rarely marks anyone as owner.",
                "The owner's, organization members' and collaborators' - people with access to the repository (recommended).",
                "Anyone's - on a public repository that's anyone with a GitHub account; only for private repositories.",
            ],
            CurrentIndex(levels, config.ReviewComments, fallback: 2),
            note: "For one repository, run `lba review-comments <level>` in its clone later.")];
    }

    /// <summary>The workspace mode (<see cref="RunnerConfig.Workspace"/>), the config's current one by default.</summary>
    private static string ChooseWorkspace(RunnerConfig config)
    {
        string[] modes = [WorkspaceModes.Worktree, WorkspaceModes.Checkout];
        return modes[ConsolePrompt.Choose(
            "Where should the agent work?",
            [
                $"In a separate git worktree per issue under {config.WorktreeFolder} - your clones aren't touched.",
                "In your own clones, one issue at a time: each switched to a new branch from the fetched default branch,\n"
                    + "so you review and run the result where you always work. Before switching a clone that has uncommitted\n"
                    + "changes or is on another branch, the agent asks you first.",
            ],
            config.InCheckouts ? 1 : 0)];
    }

    /// <summary>The index of the config's current value among <paramref name="values"/>, or <paramref name="fallback"/>.</summary>
    private static int CurrentIndex(string[] values, string current, int fallback)
    {
        var index = Array.IndexOf(values, current.ToLowerInvariant());
        return index < 0 ? fallback : index;
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
            var answer = ConsolePrompt.Ask(question);
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
            var folder = ConsolePrompt.Ask($"Folder of the {space.Key} repository (a git checkout):");
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
            var answer = ConsolePrompt.Ask(question);
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

}
