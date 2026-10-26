using System.Text.Json;
using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Infrastructure;
using Laraue.Boards.Agent.Workspaces;

namespace Laraue.Boards.Agent.Publishing;

/// <summary>A pull request of the issue's branch with its open review comments.</summary>
/// <param name="Level">Whose comments were kept, a <see cref="ReviewCommentLevels"/> value.</param>
/// <param name="Skipped">Open comments left out because their authors aren't trusted at <paramref name="Level"/>.</param>
public sealed record PullRequestReview(string Url, IReadOnlyList<PullRequestReviewComment> Comments, string Level, int Skipped);

/// <param name="Where">The file and line of a line comment ("src/Api.cs:42"), or "review" / "comment" for the rest.</param>
public sealed record PullRequestReviewComment(string Where, string Author, string Body);

/// <summary>
/// The open review comments on the issue branch's pull requests, read with the GitHub CLI by the runner - the agent can't
/// run gh, which could also write. "Open": unresolved review threads, and review summaries and conversation comments
/// newer than the branch's last commit (older ones the last run already saw). Best effort: without gh, a pull request
/// or GitHub access there's nothing to add.
/// Whose comments are kept is the person's choice per repository (<see cref="RunnerConfig.ReviewCommentsFor"/>,
/// `lba review-comments`): on a public repository anyone can comment, and the agent edits code and runs builds and tests
/// on this computer - a stranger's comment mustn't steer it.
/// </summary>
public static class PullRequestReviews
{
    /// <summary>Caps what goes into the prompt; a longer comment is cut, and further comments are left out.</summary>
    private const int MaxCommentLength = 2000;
    private const int MaxComments = 50;

    private const string Query = """
        query($owner: String!, $name: String!, $branch: String!) {
          repository(owner: $owner, name: $name) {
            pullRequests(headRefName: $branch, states: OPEN, first: 1) {
              nodes {
                url
                commits(last: 1) { nodes { commit { committedDate } } }
                reviewThreads(first: 100) {
                  nodes { isResolved path line originalLine comments(first: 50) { nodes { author { login } authorAssociation body } } }
                }
                reviews(last: 50) { nodes { author { login } authorAssociation body submittedAt } }
                comments(last: 50) { nodes { author { login } authorAssociation body createdAt } }
              }
            }
          }
        }
        """;

    public static IReadOnlyList<PullRequestReview> Find(Workspace workspace, RunnerConfig config)
    {
        if (CodingAgents.FindOnPath("gh") is not { } gh)
        {
            return [];
        }

        var reviews = new List<PullRequestReview>();
        foreach (var repo in workspace.Repos.Where(x => x.Url is not null && x.BaseCommit is not null))
        {
            var level = config.ReviewCommentsFor(Git.RemoteUrls(repo.ClonePath));
            if (level == ReviewCommentLevels.Off || Git.CommitsSince(repo.Path, repo.BaseCommit!, workspace.Branch) <= 0)
            {
                continue;
            }

            // {owner} and {repo} are filled in by gh from the clone's remote.
            var result = Publisher.RunGh(
                gh,
                repo.Path,
                ["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-F", $"branch={workspace.Branch}", "-f", $"query={Query}"]);
            if (result.ExitCode != 0)
            {
                Log.Info($"{repo.Name}: couldn't read the pull request's reviews: {result.Error.Trim()}");
                continue;
            }

            try
            {
                if (Parse(result.Output, level) is { } review && (review.Comments.Count > 0 || review.Skipped > 0))
                {
                    reviews.Add(review);
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                Log.Info($"{repo.Name}: couldn't read the pull request's reviews: {e.Message}");
            }
        }

        return reviews;
    }

    public static PullRequestReview? Parse(string json, string level)
    {
        using var document = JsonDocument.Parse(json);
        var pullRequests = document.RootElement
            .GetProperty("data")
            .GetProperty("repository")
            .GetProperty("pullRequests")
            .GetProperty("nodes");
        if (pullRequests.GetArrayLength() == 0)
        {
            return null;
        }

        var pullRequest = pullRequests[0];
        var lastCommitAt = pullRequest.GetProperty("commits").GetProperty("nodes") is { } commits && commits.GetArrayLength() > 0
            ? commits[0].GetProperty("commit").GetProperty("committedDate").GetDateTimeOffset()
            : DateTimeOffset.MinValue;

        var open = new List<(string Where, JsonElement Comment)>();
        foreach (var thread in pullRequest.GetProperty("reviewThreads").GetProperty("nodes").EnumerateArray())
        {
            if (thread.GetProperty("isResolved").GetBoolean())
            {
                continue;
            }

            var line = Number(thread, "line") ?? Number(thread, "originalLine");
            var where = $"{thread.GetProperty("path").GetString()}{(line is null ? "" : $":{line}")}";
            // A thread is its first comment and the replies to it.
            open.AddRange(thread.GetProperty("comments").GetProperty("nodes").EnumerateArray().Select(comment => (where, comment)));
        }

        open.AddRange(Newer(pullRequest.GetProperty("reviews"), "submittedAt", lastCommitAt).Select(x => ("review", x)));
        open.AddRange(Newer(pullRequest.GetProperty("comments"), "createdAt", lastCommitAt).Select(x => ("comment", x)));

        var withText = open
            .Where(x => !string.IsNullOrWhiteSpace(x.Comment.GetProperty("body").GetString()))
            .ToList();
        var trusted = withText
            .Where(x => IsTrusted(x.Comment, level))
            .ToList();
        return new PullRequestReview(
            pullRequest.GetProperty("url").GetString()!,
            trusted
                .Take(MaxComments)
                .Select(x => Comment(x.Where, x.Comment))
                .ToList(),
            level,
            withText.Count - trusted.Count);
    }

    private static bool IsTrusted(JsonElement comment, string level)
    {
        if (ReviewCommentLevels.Associations(level) is not { } associations)
        {
            return true;
        }

        return comment.TryGetProperty("authorAssociation", out var association)
            && associations.Contains(association.GetString());
    }

    private static IEnumerable<JsonElement> Newer(JsonElement connection, string dateProperty, DateTimeOffset after)
    {
        return connection.GetProperty("nodes").EnumerateArray()
            .Where(x => x.TryGetProperty(dateProperty, out var date)
                && date.ValueKind == JsonValueKind.String
                && date.GetDateTimeOffset() > after);
    }

    private static PullRequestReviewComment Comment(string where, JsonElement comment)
    {
        var author = comment.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var login)
            ? $"@{login.GetString()}"
            : "unknown";
        var body = (comment.GetProperty("body").GetString() ?? "").Trim();
        return new PullRequestReviewComment(where, author, body.Length > MaxCommentLength ? body[..MaxCommentLength] + "…" : body);
    }

    private static int? Number(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
    }
}
