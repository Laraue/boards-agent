# boards-agent

Hand a [Laraue Boards](https://boards.laraue.com) issue to Claude Code running on your own machine.

You click **Hand to agent** on an issue assigned to you. boards-agent, running on your computer with your API key, picks the issue up. It creates a git worktree on branch `agent/<KEY>` and runs Claude Code there. When Claude Code is done, the issue is in Review with a summary of what changed. Nothing is pushed: you review the branch and push it yourself.

> Work in progress: the first release isn't out yet.

## How it works

1. boards-agent polls `list_agent_runs` for Ready runs on issues assigned to you, in the spaces you mapped to local repositories.
2. It claims a run, which the server only allows for the assignee's own key and not while the issue is blocked. It moves the issue to the epic's first In Progress status and creates the worktree.
3. It starts `claude -p` in the worktree with the bundled `work-on-issue` skill. The skill:
   - reads the issue, its comments and its links
   - posts a plan, commits on the branch, and runs the build and tests
   - posts a summary, moves the issue to Review, and finishes the run
4. While Claude Code works, boards-agent checks for a cancel every 30 s and stops Claude Code if the run was cancelled or the timeout passed.

While a run is open, the issue is locked: it can't be edited or deleted. Status changes and comments still work, and anyone with edit rights can cancel the run.

## Safety

- Claude Code runs with `--permission-mode acceptEdits` and an explicit `--allowedTools` list (your build and test commands, local git, and the Boards tools it needs). Anything else is denied, since nobody is there to approve it.
- `git push`, `gh`, `WebFetch` and `WebSearch` are always denied.
- Only the Boards MCP server is loaded (`--strict-mcp-config`), and only the repository's project settings are used (`--setting-sources project`).
- The skill takes instructions only from the issue text and the assignee's own comments. Comments by anyone else are treated as untrusted data.
- Your API key is read from an environment variable and never written to disk. The MCP config passes it to Claude Code as `${BOARDS_AGENT_API_KEY}`.

## Run from source

You need the .NET 10 SDK and Claude Code (on `PATH`, or the copy bundled with the Claude desktop app).

1. Create an API key in Boards: Account -> API keys. A name like "Claude Code agent" makes the issue history read "you via API key Claude Code agent".
2. Copy `boards-agent.example.json` to `boards-agent.json` and map your spaces to local checkouts.
3. Run:

```bash
export BOARDS_AGENT_API_KEY=<your key>
dotnet run boards-agent.cs
```

`--dry-run` lists Ready runs without claiming them. `--once` polls once and exits.

## License

MIT
