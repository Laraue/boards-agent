using Laraue.Boards.Agent.Agents.Codex;

namespace Laraue.Boards.Agent.Tests.Agents;

public class CodexOutputTests
{
    private readonly CodexOutput _output = new();
    private readonly List<string> _steps = [];

    public CodexOutputTests()
    {
        _output.OnStep = _steps.Add;
    }

    [Fact]
    public void Read_ShouldTakeTheSessionFromTheThread_WhenTheThreadStarts()
    {
        _output.Read("""{"type":"thread.started","thread_id":"t1"}""");

        Assert.Equal("t1", _output.SessionId);
    }

    [Fact]
    public void Read_ShouldReportCommandsEditsAndToolCallsAsSteps_WhenTheAgentWorks()
    {
        _output.Read("""{"type":"item.started","item":{"type":"command_execution","command":"bash -lc 'dotnet test'"}}""");
        _output.Read("""{"type":"item.completed","item":{"type":"file_change","changes":[{"path":"/r/src/A.cs"},{"path":"/r/src/B.cs"}]}}""");
        _output.Read("""{"type":"item.started","item":{"type":"mcp_tool_call","server":"boards","tool":"create_comment"}}""");
        _output.Read("""{"type":"item.started","item":{"type":"reasoning"}}""");

        Assert.Equal(["dotnet test", "Edit A.cs, B.cs", "Boards: create_comment"], _steps);
    }

    [Fact]
    public void Summary_ShouldCountTurnsAndTokens_WhenTurnsComplete()
    {
        _output.Read("""{"type":"turn.completed","usage":{"input_tokens":1000,"output_tokens":200}}""");
        _output.Read("""{"type":"turn.completed","usage":{"input_tokens":500,"output_tokens":100}}""");
        _output.Read("""{"type":"item.completed","item":{"type":"agent_message","text":"Done."}}""");

        Assert.Equal("Codex: completed, 2 turns, 1,800 tokens (1,500 in, 300 out).", _output.Summary());
        Assert.Equal("Done.", _output.FinalMessage());
    }

    [Fact]
    public void Summary_ShouldGiveTheError_WhenATurnFails()
    {
        _output.Read("""{"type":"turn.failed","error":{"message":"Rate limited"}}""");

        Assert.Equal("Codex: failed, 1 turn, 0 tokens (0 in, 0 out). Rate limited", _output.Summary());
    }
}
