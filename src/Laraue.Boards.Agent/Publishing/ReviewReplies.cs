using System.Text.RegularExpressions;
using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Publishing;

/// <summary>A reply the runner posts on a pull request, in a review thread or (<paramref name="ThreadId"/> null) as a comment.</summary>
/// <param name="Resolve">Whether to resolve the thread after replying: every comment in it was fixed and resolving is on.</param>
public sealed record ReviewReply(string RepoPath, string PullRequestUrl, string? ThreadId, string Body, bool Resolve);

/// <summary>
/// After a re-run that got review comments: the agent's outcome for each (its final message ends with lines like
/// "R1: fixed - ..."), posted on the pull request by the runner with the GitHub CLI - in each review thread, and one
/// comment for review summaries and conversation comments. Only once the branch is pushed, so a reply never points at
/// commits the reviewer can't see. The agent itself never writes to GitHub.
/// </summary>
public static partial class ReviewReplies
{
    private const string ReplyMutation = """
        mutation($id: ID!, $body: String!) {
          addPullRequestReviewThreadReply(input: { pullRequestReviewThreadId: $id, body: $body }) { comment { id } }
        }
        """;

    private const string ResolveMutation = """
        mutation($id: ID!) {
          resolveReviewThread(input: { threadId: $id }) { thread { isResolved } }
        }
        """;

    /// <summary>The agent's outcome lines, by reference: "R1: fixed - moved it" -> R1 = "fixed - moved it". Later lines win.</summary>
    public static IReadOnlyDictionary<string, string> ParseOutcomes(string? finalMessage)
    {
        var outcomes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (finalMessage ?? "").Split('\n'))
        {
            if (OutcomeLine().Match(line) is { Success: true } match)
            {
                outcomes[match.Groups["ref"].Value.ToUpperInvariant()] = match.Groups["text"].Value.Trim().TrimEnd('`');
            }
        }

        return outcomes;
    }

    /// <summary>
    /// The replies to post: one per review thread with an outcome, and one comment per pull request for its other
    /// comments with outcomes. A comment without an outcome line gets no reply.
    /// </summary>
    /// <param name="commit">The pushed commit the replies refer to, shortened; null to leave it out.</param>
    public static IReadOnlyList<ReviewReply> Plan(IReadOnlyList<PullRequestReview> reviews, string? finalMessage, bool resolveFixed, string? commit)
    {
        var outcomes = ParseOutcomes(finalMessage);
        var signature = $"\n\n_laraue-boards-agent{(commit is null ? "" : $", {commit}")}_";
        var replies = new List<ReviewReply>();
        foreach (var review in PullRequestReviews.Numbered(reviews).Where(x => x.Review.RepoPath is not null).GroupBy(x => x.Review))
        {
            var repoPath = review.Key.RepoPath!;
            foreach (var thread in review.Where(x => x.Comment.ThreadId is not null).GroupBy(x => x.Comment.ThreadId!))
            {
                var answered = thread.Where(x => outcomes.ContainsKey(x.Ref)).ToList();
                if (answered.Count == 0)
                {
                    continue;
                }

                var body = answered.Count == 1
                    ? outcomes[answered[0].Ref]
                    : string.Join("\n", answered.Select(x => $"- {x.Comment.Author}: {outcomes[x.Ref]}"));
                var allFixed = answered.Count == thread.Count() && answered.All(x => IsFixed(outcomes[x.Ref]));
                replies.Add(new ReviewReply(repoPath, review.Key.Url, thread.Key, body + signature, resolveFixed && allFixed));
            }

            var others = review
                .Where(x => x.Comment.ThreadId is null && outcomes.ContainsKey(x.Ref))
                .Select(x => $"- {x.Comment.Author}'s {x.Comment.Where}: {outcomes[x.Ref]}")
                .ToList();
            if (others.Count > 0)
            {
                replies.Add(new ReviewReply(repoPath, review.Key.Url, ThreadId: null, "On the review:\n" + string.Join("\n", others) + signature, Resolve: false));
            }
        }

        return replies;
    }

    /// <summary>
    /// Posts the replies for the run's reviews once their branch is pushed, and returns one line per pull request for the
    /// runner's comment, e.g. "https://github.com/acme/api/pull/3: 2 review threads replied to (1 resolved)".
    /// </summary>
    public static IReadOnlyList<string> Post(RunnerConfig config, Workspace workspace, string? finalMessage)
    {
        if (workspace.Reviews.Count == 0 || CodingAgents.FindOnPath("gh") is not { } gh)
        {
            return [];
        }

        var report = new List<string>();
        foreach (var review in workspace.Reviews.Where(x => x.RepoPath is not null))
        {
            var repo = review.RepoPath!;
            if (Git.PushedCommit(repo, workspace.Branch) is not { } pushed)
            {
                Log.Info($"{review.Url}: {workspace.Branch} isn't pushed, so the review comments get no replies.");
                continue;
            }

            var replies = Plan([review], finalMessage, config.ResolveReviewThreads, pushed[..Math.Min(7, pushed.Length)]);
            var threads = 0;
            var resolved = 0;
            var comments = 0;
            foreach (var reply in replies)
            {
                if (reply.ThreadId is { } threadId)
                {
                    if (!Run(gh, repo, ["api", "graphql", "-f", $"query={ReplyMutation}", "-f", $"id={threadId}", "-f", $"body={reply.Body}"]))
                    {
                        continue;
                    }

                    threads++;
                    if (reply.Resolve && Run(gh, repo, ["api", "graphql", "-f", $"query={ResolveMutation}", "-f", $"id={threadId}"]))
                    {
                        resolved++;
                    }
                }
                else if (Run(gh, repo, ["pr", "comment", reply.PullRequestUrl, "--body", reply.Body]))
                {
                    comments++;
                }
            }

            if (threads + comments > 0)
            {
                var parts = new List<string>();
                if (threads > 0)
                {
                    parts.Add($"{threads} review thread{(threads == 1 ? "" : "s")} replied to{(resolved > 0 ? $" ({resolved} resolved)" : "")}");
                }

                if (comments > 0)
                {
                    parts.Add("the review summarized in a comment");
                }

                report.Add($"{review.Url}: {string.Join(", ", parts)}");
            }
        }

        return report;
    }

    /// <summary>"fixed", "Fixed:", "fixed -": what the prompt asks the agent to start a fixed comment's line with.</summary>
    private static bool IsFixed(string outcome)
    {
        return outcome.StartsWith("fixed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Run(string gh, string repo, string[] arguments)
    {
        var result = Publisher.RunGh(gh, repo, arguments);
        if (result.ExitCode != 0)
        {
            Log.Error($"gh {arguments[0]} {arguments[1]} in {repo} failed: {result.Error.Trim()}");
        }

        return result.ExitCode == 0;
    }

    /// <summary>"R1: text", "- R2 - text", "[R3] text", "`R4: text`" - a reference, then the outcome.</summary>
    [GeneratedRegex(@"^\s*[-*]?\s*`?\[?(?<ref>R\d+)\]?\s*[:\-–—]?\s+(?<text>\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OutcomeLine();
}
