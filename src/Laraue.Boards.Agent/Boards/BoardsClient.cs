using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Laraue.Boards.Agent.Infrastructure;

namespace Laraue.Boards.Agent.Boards;

/// <summary>
/// The Boards MCP tools the runner calls. A call that fails drops the connection and the next call opens a new one: an
/// MCP session doesn't survive Boards restarting, and a broken one would fail every call after.
/// </summary>
public sealed class BoardsClient(string url, string apiKey, McpClient connected) : IBoardsClient
{
    private const int PageSize = 50;

    private McpClient? _client = connected;

    public static async Task<BoardsClient> ConnectAsync(string url, string apiKey, CancellationToken ct)
    {
        return new BoardsClient(url, apiKey, await Connect(url, apiKey, ct));
    }

    private static Task<McpClient> Connect(string url, string apiKey, CancellationToken ct)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(url),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
                Name = "boards-agent",
            });

        return McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    public Task<MeInfo> GetMe(CancellationToken ct)
    {
        return Call("get_me", new Dictionary<string, object?>(), RunnerJson.Default.MeInfo, ct);
    }

    public Task<IReadOnlyList<SpaceSummary>> ListSpaces(CancellationToken ct)
    {
        return Call("list_spaces", new Dictionary<string, object?>(), RunnerJson.Default.IReadOnlyListSpaceSummary, ct);
    }

    public Task<SpaceDetail> GetSpace(string spaceKey, CancellationToken ct)
    {
        return Call("get_space", new Dictionary<string, object?> { ["spaceKey"] = spaceKey }, RunnerJson.Default.SpaceDetail, ct);
    }

    public async Task<IReadOnlyList<AgentRunSummary>> ListReadyRuns(string? spaceKey, Guid userId, CancellationToken ct)
    {
        var result = new List<AgentRunSummary>();
        for (var page = 0; ; page++)
        {
            var runs = await Call(
                "list_agent_runs",
                new Dictionary<string, object?>
                {
                    ["spaceKey"] = spaceKey,
                    ["state"] = "Ready",
                    // The SDK serializes arguments with its own JSON context, which has string[] but not Guid[].
                    ["assigneeIds"] = new[] { userId.ToString() },
                    ["excludeBlocked"] = true,
                    ["page"] = page,
                    ["count"] = PageSize,
                },
                RunnerJson.Default.AgentRunListPage,
                ct);
            result.AddRange(runs.Runs);
            if (!runs.HasNextPage)
            {
                return result;
            }
        }
    }

    public Task<bool> ClaimAgentRun(long runId, CancellationToken ct)
    {
        return Call("claim_agent_run", new Dictionary<string, object?> { ["runId"] = runId }, RunnerJson.Default.Boolean, ct);
    }

    public Task<AgentRunSummary> StartAgentRun(string issueKey, CancellationToken ct)
    {
        return Call("start_agent_run", new Dictionary<string, object?> { ["issueKey"] = issueKey }, RunnerJson.Default.AgentRunSummary, ct);
    }

    public Task<bool> CancelAgentRun(long runId, CancellationToken ct)
    {
        return Call("cancel_agent_run", new Dictionary<string, object?> { ["runId"] = runId }, RunnerJson.Default.Boolean, ct);
    }

    public Task<bool> FinishAgentRun(long runId, string state, string? reason, CancellationToken ct)
    {
        return Call(
            "finish_agent_run",
            new Dictionary<string, object?> { ["runId"] = runId, ["state"] = state, ["reason"] = reason },
            RunnerJson.Default.Boolean,
            ct);
    }

    public Task<IReadOnlyList<EpicStatusSummary>> ListStatuses(string spaceKey, CancellationToken ct)
    {
        return Call("list_statuses", new Dictionary<string, object?> { ["spaceKey"] = spaceKey }, RunnerJson.Default.IReadOnlyListEpicStatusSummary, ct);
    }

    public Task<IssueDetail> GetIssue(string issueKey, CancellationToken ct)
    {
        return Call("get_issue", new Dictionary<string, object?> { ["issueKey"] = issueKey }, RunnerJson.Default.IssueDetail, ct);
    }

    public Task EditIssueStatus(string issueKey, long statusId, string? comment, CancellationToken ct)
    {
        return CallVoid(
            "edit_issue_status",
            new Dictionary<string, object?> { ["issueKey"] = issueKey, ["statusId"] = statusId, ["comment"] = comment },
            ct);
    }

    public Task CreateComment(string issueKey, string text, CancellationToken ct)
    {
        return CallVoid("create_comment", new Dictionary<string, object?> { ["issueKey"] = issueKey, ["text"] = text }, ct);
    }

    public async Task<IReadOnlyList<IssueComment>> ListIssueCommentsFrom(string issueKey, int skip, CancellationToken ct)
    {
        var result = new List<IssueComment>();
        for (var page = skip / PageSize; ; page++)
        {
            var comments = await Call(
                "list_issue_comments",
                new Dictionary<string, object?> { ["issueKey"] = issueKey, ["page"] = page, ["count"] = PageSize },
                RunnerJson.Default.IssueCommentPage,
                ct);
            result.AddRange(comments.Comments.Skip(page == skip / PageSize ? skip % PageSize : 0));
            if (!comments.HasNextPage)
            {
                return result;
            }
        }
    }

    private async Task<T> Call<T>(string tool, IReadOnlyDictionary<string, object?> arguments, JsonTypeInfo<T> result, CancellationToken ct)
    {
        var text = await CallVoid(tool, arguments, ct);
        return JsonSerializer.Deserialize(text, result)
            ?? throw new BoardsToolException(tool, "empty result");
    }

    private async Task<string> CallVoid(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            _client ??= await Connect(url, apiKey, ct);
            result = await _client.CallToolAsync(tool, arguments, cancellationToken: ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            await Disconnect();
            throw;
        }

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(x => x.Text));
        if (result.IsError == true)
        {
            throw new BoardsToolException(tool, text);
        }

        return text;
    }

    public async ValueTask DisposeAsync()
    {
        await Disconnect();
    }

    private async Task Disconnect()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception e)
            {
                Log.Error($"Closing the Boards connection failed: {e.Message}");
            }
        }
    }
}

public sealed class BoardsToolException(string tool, string message) : Exception($"{tool}: {message}");
