# boards-agent

Hand a [Laraue Boards](https://boards.laraue.com) issue to Claude Code running on your own machine.

You click **Hand to agent** on an issue assigned to you. boards-agent, running on your computer with your API key, picks the issue up. It creates a git worktree on branch `agent/<KEY>` and runs Claude Code there. When Claude Code is done, the issue is in Review with a summary of what changed. Nothing is pushed: you review the branch and push it yourself.

## How it works

1. boards-agent polls `list_agent_runs` for Ready runs on issues assigned to you, in the spaces you mapped to local repositories.
2. It claims a run, which the server only allows for the assignee's own key and not while the issue is blocked. It moves the issue to the epic's first In Progress status, fetches the base branch (e.g. `origin/main`) and creates the worktree.
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
- Your API key is issued by Boards when you click Allow during `boards-agent setup`, and kept in the macOS Keychain, the Windows Credential Manager, or the Secret Service on Linux. It's never shown or written to a file. Claude Code gets it through its environment: the MCP config refers to it as `${BOARDS_AGENT_API_KEY}`.

## Install

You need [Claude Code](https://claude.com/claude-code) (or the Claude desktop app, which bundles it) and git. Then run one command; it installs boards-agent and starts `boards-agent setup`.

macOS and Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/Laraue/boards-agent/main/install.sh | sh
```

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/Laraue/boards-agent/main/install.ps1 | iex
```

The script downloads the newest release for your system, checks it against the release's `SHA256SUMS`, and puts the binary in `~/.local/bin` (`%LOCALAPPDATA%\Programs\boards-agent` on Windows), adding that folder to your PATH. Running it again updates boards-agent and restarts the background service. Each release is a single self-contained binary, with no .NET needed; you can also download it from [Releases](https://github.com/Laraue/boards-agent/releases) yourself.

After setup, you don't run anything: click **Hand to agent** on an issue assigned to you in Boards.

## Setup

Run `boards-agent setup`, ideally from inside the repository you want the agent to work in. It:

1. Opens Boards in your browser. Check the code, pick the organization and click **Allow**. Boards creates an API key named "Claude Code agent (your machine)" and hands it to the runner, which keeps it in the Keychain. You can revoke the key any time under Account -> API keys.
2. Asks which space's issues are worked on in which local repository, offering the one you ran it in. New branches start from the remote's default branch (e.g. `origin/main`).
3. Finds Claude Code and runs `claude auth login` if it isn't signed in.
4. Offers to run the agent in the background (macOS and Windows), so there's no terminal to keep open. After that, clicking **Hand to agent** in Boards is all there is to it.

The answers go to `~/.config/boards-agent/config.json` (`%APPDATA%\boards-agent\config.json` on Windows), which the runner reads by default. Running setup again keeps what's there. Use `--no-browser` to just print the link, e.g. over SSH. `--config <file>` points both setup and the runner at another config file; `boards-agent.example.json` lists every setting. Setting `BOARDS_AGENT_API_KEY` overrides the stored key.

The macOS binaries aren't notarized yet. If macOS refuses to open the binary, allow it in System Settings -> Privacy & Security, or run `xattr -d com.apple.quarantine boards-agent`.

## Background service

`boards-agent service install` runs the runner in the background as you, starting it now and whenever you sign in, and restarting it if it stops. Setup offers to do this for you.

- macOS: a launchd LaunchAgent. It keeps the `PATH` of the terminal you install it from, so Claude Code finds your build tools. Log: `~/Library/Logs/boards-agent/boards-agent.log`.
- Windows: a Task Scheduler task that runs at logon, without a window. Log: `%LOCALAPPDATA%\boards-agent\boards-agent.log`.
- Linux (systemd) isn't supported yet: run `boards-agent` in a terminal there.

`boards-agent service status` shows whether it runs and the end of its log; `boards-agent service uninstall` removes it. Stopping the service on macOS fails a run in progress, so the issue isn't left locked. On Windows a stopped task can't report that: cancel the run in Boards.

## Commands

`boards-agent` runs in the foreground. `--check` prints the version, which Claude Code it found, and who your key signs in as. `--dry-run` lists Ready runs without claiming them. `--once` polls once and exits.

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
