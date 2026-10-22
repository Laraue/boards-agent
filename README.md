# laraue-boards-agent (lba)

Hand a [Laraue Boards](https://boards.laraue.com) issue to a coding agent - Claude Code or OpenAI Codex - running on your own machine. The command is `laraue-boards-agent`, or `lba` for short (before 0.4 it was `boards-agent`, which an update keeps as a link).

You click **Hand to agent** on an issue assigned to you. boards-agent, running on your computer with your API key, picks the issue up. It puts the work on branch `agent/<KEY>-<title>` (e.g. `agent/BRD-314-sort-epics-in-space-detail-page`; a re-run of the issue reuses its branch even if the issue was renamed) - in a separate git worktree, or in your own clone if you prefer (see [Where the agent works](#where-the-agent-works)) - and runs the agent there. When the agent is done, the issue is in Review with a summary of what changed, and you get a notification. By default nothing is pushed: you review the branch and push it yourself - or let boards-agent push it and open a pull request (see [When the agent is done](#when-the-agent-is-done)).

## How it works

1. boards-agent polls `list_agent_runs` for Ready runs on issues assigned to you, in every space. A space's repositories are the links on the space in Boards (space settings) whose URL matches the git remote of a clone under your code folder - so adding a repository is adding a link in Boards; nothing is mapped by hand. A run in a space with no repository cloned on this machine is left for another machine.
2. It claims a run, which the server only allows for the assignee's own key and not while the issue is blocked. It moves the issue to the epic's first In Progress status, fetches the base branch (e.g. `origin/main`) and creates the worktree. A space with several repositories gets a folder with a worktree per repository, all on the issue's branch; the agent changes the ones the issue needs, and the others are removed when the run ends. The agent also gets the space's description and links as context.
3. It starts `claude -p` in the worktree with the bundled `work-on-issue` skill. The skill:
   - reads the issue, its comments and its links
   - posts a plan, commits on the branch, and runs the build and tests
   - posts a summary, moves the issue to Review, and finishes the run
4. While the agent works, boards-agent checks for a cancel every 30 s and stops the agent if the run was cancelled or the timeout passed. A dropped connection to Boards is retried at the next check, not the end of the run. If boards-agent itself stops while the agent works (a crash, a restart of the computer), it fails that run as soon as it starts again, with a comment saying which branches have commits, so the issue isn't left locked.

While a run is open, the issue is locked: it can't be edited or deleted. Status changes and comments still work, and anyone with edit rights can cancel the run.

## Where the agent works

Setup asks; the `workspace` setting in the config holds the answer.

- **`worktree`** (the default): a separate git worktree per issue under `worktreeFolder` (`~/boards-agent-worktrees`). Your clones aren't touched, so you can keep working in them while the agent works. Branches without commits are removed when the run ends.
- **`checkout`**: your own clones, one issue at a time. For each of the space's repositories, boards-agent fetches the default branch and switches the clone to the issue's new branch from it (your local `main` isn't touched). When the run ends, the clones the agent committed to stay on that branch: open them in your IDE, run the app and review as you always do. The others go back to the branch they were on, and their empty branch is deleted.

  Before switching, boards-agent checks every clone. A clean clone is switched without asking, whatever branch it's on (for example, the previous issue's, while you review it): its commits stay on that branch. Only a clone with uncommitted changes makes it ask you first - in the terminal, or with a dialog when it runs in the background - since switching could carry them along or clash with them.
  - **Continue**: checks again. A clone that still has uncommitted changes makes it ask again: your changes are never stashed or discarded, so commit or stash them yourself.
  - **Not now**: the run stays Ready, and you're asked again in 10 minutes. A dialog nobody answers counts as Not now after 10 minutes.

  Claude Code starts in the first repository and gets the others with `--add-dir`; Codex gets them as writable folders of its sandbox. Don't edit those clones while the agent works in them.

## When the agent is done

- **Notifications.** boards-agent shows a system notification, as **laraue-boards-agent** with the Laraue logo, when a run starts, is ready for review, fails or is cancelled. Clicking it opens the issue. On macOS they come from a tiny app boards-agent creates in `~/Applications/laraue-boards-agent.app`, since macOS names a notification after the app that sends it; the first one may ask whether it may notify you. `"notifications": false` in the config turns them off.
- **Publishing** (`publish`, asked in setup, `off` by default):
  - `off`: the branch stays on your computer.
  - `push`: after a run ends Done, boards-agent pushes the issue's branch in every repository the agent committed to.
  - `pr`: it also opens a pull request with the [GitHub CLI](https://cli.github.com) (`gh`, signed in), or finds the one an earlier run of the issue opened.
  The runner's comment on the issue and the review notification carry the branches and pull requests. The agent itself still can't push: boards-agent does, after the run.
- **Feedback, right here: `lba continue <KEY>`.** Opens the agent's session of that issue in your terminal - the same Claude Code or Codex conversation, with everything it knew, in the same repositories and on the issue's branch. Talk to it ("the button is still blue", "add a test"), see the changes and run the app straight away: no status changes in Boards, no waiting. `lba continue <KEY> "make the button red"` does one round without you, unattended like a run. When you're done, one summary comment with the new commits goes to the issue (and with `publish` on, the branch is pushed and the pull request updated). In checkout mode, your clones are switched back to the issue's branch first, asking about uncommitted changes as a run does. Only the computer that ran the issue has its session.
- **Feedback from Boards.** **Send back to agent** on an issue under review takes your comment, moves the issue back and hands it off again; the next run continues on the same branch ("continuing ... (N commits so far)") and follows your comments. Handy when you're away from the computer.

## Safety

- Claude Code runs with `--permission-mode acceptEdits` and an explicit `--allowedTools` list (your build and test commands, local git, and the Boards tools it needs). Anything else is denied, since nobody is there to approve it.
- Codex runs as `codex exec` in its `workspace-write` sandbox: it can only change files in the worktree, its commands have no network access (so nothing can be pushed or sent anywhere), it never asks for approval, and web search is off. Turn on `codexNetworkAccess` in the config if your builds must download packages; that also makes `git push` possible. Codex has no turn or cost limit, so the timeout is its only limit.
- `git push`, `gh`, `WebFetch` and `WebSearch` are always denied to the agent. Pushing and pull requests, when you turn them on (`publish`), are done by boards-agent itself after the run.
- Only the Boards MCP server is loaded, with only the tools the agent needs: Claude Code with `--strict-mcp-config` and only the repository's project settings (`--setting-sources project`), Codex with `--ignore-user-config`, so your own `~/.codex/config.toml` isn't used.
- The skill takes instructions only from the issue text and the assignee's own comments. Comments by anyone else are treated as untrusted data.
- Your API key is issued by Boards when you click Allow during `lba setup`, and kept in the macOS Keychain, the Windows Credential Manager, or the Secret Service on Linux. It's never shown or written to a file. The agent gets it through its environment (`BOARDS_AGENT_API_KEY`), which its MCP connection reads; Codex's commands don't see it.

## Install

You need git and a coding agent: [Claude Code](https://claude.com/claude-code) (or the Claude desktop app, which bundles it) or [Codex](https://developers.openai.com/codex/cli). Then run one command; it installs boards-agent and starts `lba setup`.

macOS and Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/Laraue/boards-agent/main/install.sh | sh
```

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/Laraue/boards-agent/main/install.ps1 | iex
```

The script downloads the newest release for your system, checks it against the release's `SHA256SUMS`, and puts the binary in `~/.local/bin` (`%LOCALAPPDATA%\Programs\boards-agent` on Windows), adding that folder to your PATH. Running it again updates boards-agent and restarts the background service, without asking setup's questions again (run `lba setup` yourself to change them). Each release is a single self-contained binary, with no .NET needed; you can also download it from [Releases](https://github.com/Laraue/boards-agent/releases) yourself.

After setup, you don't run anything: click **Hand to agent** on an issue assigned to you in Boards.

## Setup

Run `lba setup`, ideally from inside the repository you want the agent to work in. It:

1. Picks the coding agent: the one you have installed, or asks when you have both.
2. Opens Boards in your browser. Check the code, pick the organization and click **Allow**. Boards creates an API key named after the agent and your machine, e.g. "Claude Code agent (MacBook)", and hands it to the runner, which keeps it in the Keychain. You can revoke the key any time under Account -> API keys.
3. Asks which folder your repositories are cloned under (e.g. `~/Repos`; searched three levels deep) - type it, or choose it in Finder or Explorer - and shows, per space, which of its repositories it found there by git remote, and which aren't cloned. New branches start from each remote's default branch (e.g. `origin/main`). If no space links to a repository yet, add the repository's URL as a link on the space in Boards, or map one by hand.
4. Asks where the agent should work: a worktree per issue, or your own clones (see [Where the agent works](#where-the-agent-works)).
5. Asks what happens to the agent's branch when it's done: nothing, push it, or push it and open a pull request (see [When the agent is done](#when-the-agent-is-done)).
6. Signs the agent in if it isn't (`claude auth login` or `codex login`).
7. Offers to run the agent in the background (macOS and Windows), so there's no terminal to keep open. After that, clicking **Hand to agent** in Boards is all there is to it.

The answers go to `~/.config/boards-agent/config.json` (`%APPDATA%\boards-agent\config.json` on Windows), which the runner reads by default. Running setup again keeps what's there. Use `--no-browser` to just print the link, e.g. over SSH. `--config <file>` points both setup and the runner at another config file; `boards-agent.example.json` lists every setting. Setting `BOARDS_AGENT_API_KEY` overrides the stored key.

The macOS binaries aren't notarized yet. If macOS refuses to open the binary, allow it in System Settings -> Privacy & Security, or run `xattr -d com.apple.quarantine boards-agent`.

## Background service

`lba service install` runs the runner in the background as you, starting it now and whenever you sign in, and restarting it if it stops. Setup offers to do this for you.

- macOS: a launchd LaunchAgent. It keeps the `PATH` of the terminal you install it from, so the agent finds your build tools. Log: `~/Library/Logs/boards-agent/boards-agent.log`.
- Windows: a Task Scheduler task that runs at logon, without a window. Log: `%LOCALAPPDATA%\boards-agent\boards-agent.log`.
- Linux (systemd) isn't supported yet: run `lba` in a terminal there.

`lba service status` (or just `lba status`) shows whether it runs and the end of its log; `lba service restart` restarts it (the install script does after an update); `lba service uninstall` removes it. Stopping the service on macOS fails a run in progress, so the issue isn't left locked. On Windows a stopped task can't report that: cancel the run in Boards.

## Commands

`lba` (or `laraue-boards-agent`) runs in the foreground. `--check` prints the version, which agent it found, and who your key signs in as. `--dry-run` lists Ready runs without claiming them. `--once` polls once and exits.

## Release

Push a `v*` tag (e.g. `v0.1.0`; `v0.1.0-beta.1` makes a pre-release). The Release workflow builds the binaries and publishes them as a GitHub release.

## Run from source

You need the .NET 10 SDK and Claude Code (on `PATH`, or the copy bundled with the Claude desktop app) or Codex (on `PATH`).

```bash
dotnet run boards-agent.cs -- setup
dotnet run boards-agent.cs
```

Against a local Boards, point setup at it: `--web-url http://localhost:3000 --api-url http://localhost:5200 --mcp-url http://localhost:5202/mcp`.

## License

MIT
