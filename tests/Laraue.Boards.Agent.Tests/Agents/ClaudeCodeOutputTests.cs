using Laraue.Boards.Agent.Agents.ClaudeCode;

namespace Laraue.Boards.Agent.Tests.Agents;

public class ClaudeCodeOutputTests
{
    private readonly ClaudeCodeOutput _output = new();
    private readonly List<string> _steps = [];

    public ClaudeCodeOutputTests()
    {
        _output.OnStep = _steps.Add;
    }

    [Fact]
    public void Read_ShouldTakeTheSessionFromTheFirstMessage_WhenEveryMessageCarriesIt()
    {
        _output.Read("""{"type":"system","subtype":"init","session_id":"first"}""");
        _output.Read("""{"type":"assistant","session_id":"second","message":{"content":[]}}""");

        Assert.Equal("first", _output.SessionId);
    }

    [Fact]
    public void Read_ShouldReportEachToolCallAsAStep_WhenTheAgentCallsTools()
    {
        _output.Read("""
            {"type":"assistant","message":{"content":[
              {"type":"text","text":"Let me check."},
              {"type":"tool_use","name":"Bash","input":{"command":"dotnet test","description":"Run the tests"}},
              {"type":"tool_use","name":"Bash","input":{"command":"git status"}},
              {"type":"tool_use","name":"Edit","input":{"file_path":"/repo/src/IssuesService.cs"}},
              {"type":"tool_use","name":"Grep","input":{"pattern":"TouchEpics"}},
              {"type":"tool_use","name":"TodoWrite","input":{}},
              {"type":"tool_use","name":"mcp__boards__create_comment","input":{}},
              {"type":"tool_use","name":"WebFetch","input":{}}]}}
            """.ReplaceLineEndings(""));

        Assert.Equal(
            ["Run the tests", "git status", "Edit IssuesService.cs", "Search TouchEpics", "Boards: create_comment", "WebFetch"],
            _steps);
    }

    [Fact]
    public void Read_ShouldCutALongStepToOneLine_WhenTheCommandIsLong()
    {
        var command = new string('x', 200) + "\\nsecond line";
        _output.Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"COMMAND"}}]}}""".Replace("COMMAND", command));

        var step = Assert.Single(_steps);
        Assert.Equal(120, step.Length);
        Assert.EndsWith("…", step);
    }

    [Fact]
    public void Summary_ShouldGiveTheOutcomeTurnsAndCost_WhenTheResultCame()
    {
        _output.Read("""{"type":"result","subtype":"success","is_error":false,"num_turns":9,"total_cost_usd":2.4,"result":"All done."}""");

        Assert.Equal("Claude Code: completed, 9 turns, $2.40.", _output.Summary());
        Assert.Equal("All done.", _output.FinalMessage());
        Assert.False(_output.HitTurnLimit);
    }

    [Fact]
    public void Summary_ShouldAddTheError_WhenTheResultIsAnError()
    {
        _output.Read("""{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":3,"total_cost_usd":0.1,"result":"API error"}""");

        Assert.Equal("Claude Code: failed (error during execution), 3 turns, $0.10. API error.", _output.Summary());
    }

    [Fact]
    public void Summary_ShouldSayFailedAndHowToSignIn_WhenTheSignInExpired()
    {
        // Claude Code ends a failed sign-in with subtype "success" and is_error true.
        _output.Read("""{"type":"result","subtype":"success","is_error":true,"num_turns":1,"total_cost_usd":0,"result":"Failed to authenticate: OAuth session expired and could not be refreshed"}""");

        Assert.Equal(
            "Claude Code: failed, 1 turn, $0.00. Failed to authenticate: OAuth session expired and could not be refreshed. "
                + "To sign Claude Code in again, run `claude auth login` on this computer.",
            _output.Summary());
    }

    [Fact]
    public void Summary_ShouldSayOutOfTurns_WhenTheAgentRanOutOfTurns()
    {
        _output.Read("""{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":150,"total_cost_usd":5}""");

        Assert.Equal("Claude Code: failed (out of turns), 150 turns, $5.00.", _output.Summary());
    }

    [Fact]
    public void HitTurnLimit_ShouldBeTrue_WhenTheAgentRanOutOfTurns()
    {
        _output.Read("""{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":150,"total_cost_usd":5}""");

        Assert.True(_output.HitTurnLimit);
    }

    [Fact]
    public void Summary_ShouldSayThereIsNoResult_WhenTheAgentStoppedBeforeOne()
    {
        _output.Read("not json at all");

        Assert.Equal("No result from Claude Code.", _output.Summary());
        Assert.Null(_output.FinalMessage());
        Assert.Null(_output.SessionId);
    }
}
