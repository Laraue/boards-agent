using System.Text.Json;
using System.Text.Json.Serialization;
using Laraue.Boards.Agent.Agents;
using Laraue.Boards.Agent.Agents.ClaudeCode;
using Laraue.Boards.Agent.Config;
using Laraue.Boards.Agent.Runs;
using Laraue.Boards.Agent.Updates;

namespace Laraue.Boards.Agent.Boards;

/// <summary>Source-generated JSON for everything the runner reads, so a trimmed build keeps working.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunnerConfig))]
[JsonSerializable(typeof(UpdateCheckState))]
[JsonSerializable(typeof(IReadOnlyList<SpaceSummary>))]
[JsonSerializable(typeof(SpaceDetail))]
[JsonSerializable(typeof(ClaudeAuthStatus))]
[JsonSerializable(typeof(AgentSignInStart))]
[JsonSerializable(typeof(AgentSignInPoll))]
[JsonSerializable(typeof(MeInfo))]
[JsonSerializable(typeof(AgentRunListPage))]
[JsonSerializable(typeof(AgentRunSummary))]
[JsonSerializable(typeof(IReadOnlyList<EpicStatusSummary>))]
[JsonSerializable(typeof(IssueDetail))]
[JsonSerializable(typeof(IssueCommentPage))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(RunInProgress))]
[JsonSerializable(typeof(AgentSession))]
public partial class RunnerJson : JsonSerializerContext;
