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
- Your API key is issued by Boards when you click Allow during `boards-agent setup`, and kept in the macOS Keychain (the Secret Service on Linux). It's never shown or written to a file. Claude Code gets it through its environment: the MCP config refers to it as `${BOARDS_AGENT_API_KEY}`.

## Install

Download the archive for your system from [Releases](https://github.com/Laraue/boards-agent/releases): `osx-arm64` (Apple Silicon), `osx-x64`, `linux-x64`, `linux-arm64` or `win-x64`. Each one is a single self-contained binary, with no .NET needed, plus the README, the license and an example config. `SHA256SUMS` lists the checksums.

```bash
tar -xzf boards-agent-v0.1.0-osx-arm64.tar.gz
cd boards-agent
./boards-agent setup
./boards-agent
```

## Setup

Run `boards-agent setup`, ideally from inside the repository you want the agent to work in. It:

1. Opens Boards in your browser. Check the code, pick the organization and click **Allow**. Boards creates an API key named "Claude Code agent (your machine)" and hands it to the runner, which keeps it in the Keychain. You can revoke the key any time under Account -> API keys.
2. Asks which space's issues are worked on in which local repository, offering the one you ran it in. New branches start from the remote's default branch (e.g. `origin/main`).
3. Finds Claude Code and runs `claude auth login` if it isn't signed in.

The answers go to `~/.config/boards-agent/config.json` (`%APPDATA%\boards-agent\config.json` on Windows), which the runner reads by default. Running setup again keeps what's there. Use `--no-browser` to just print the link, e.g. over SSH. `--config <file>` points both setup and the runner at another config file; `boards-agent.example.json` lists every setting. Setting `BOARDS_AGENT_API_KEY` overrides the stored key.

The macOS binaries aren't notarized yet. If macOS refuses to open the binary, allow it in System Settings -> Privacy & Security, or run `xattr -d com.apple.quarantine boards-agent`.

`--check` prints the version, which Claude Code it found, and who your key signs in as. `--dry-run` lists Ready runs without claiming them. `--once` polls once and exits.

## Release

Push a `v*` tag (e.g. `v0.1.0`; `v0.1.0-beta.1` makes a pre-release). The Release workflow builds the binaries and publishes them as a GitHub release.

## Run from source

You need the .NET 10 SDK and Claude Code (on `PATH`, or the copy bundled with the Claude desktop app).

```bash
dotnet run boards-agent.cs -- setup
dotnet run boards-agent.cs
```

Against a local Boards, point setup at it: `--web-url http://localhost:3000 --api-url http://localhost:5200 --mcp-url http://localhost:5202/mcp`.

## License

MIT
