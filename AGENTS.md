# AGENTS.md

Conventions and gotchas for AI agents in this repo that aren't obvious from the code.

## What this project is

`laraue-boards-agent` (`lba`) runs on a developer's computer and hands Laraue Boards issues to a coding agent (Claude
Code or Codex). Someone clicks **Hand to agent** in Boards; the runner claims the run through the Boards MCP server,
puts the work on branch `agent/<KEY>-<slug>` (a worktree per issue, or the person's own clones in checkout mode),
starts the agent with the embedded `work-on-issue` skill, and reports back: a comment, the issue moved to Review, the
run finished, optionally the branch pushed and a pull request opened. `lba continue` resumes the agent's session of an
issue in the terminal. See [README.md](README.md) for the user-facing overview.

Related repositories: the Boards backend (`Laraue/Laraue.Apps.Boards` - the MCP tools and agent runs) and the web app
(`Laraue/laraue-boards` - the hand-off UI and the docs under `content/docs/{en,ru}/integrations/agent-hand-off.md`).
A change visible to users usually needs those docs updated too.

## Project layout

Solution: `boards-agent.slnx`.

- `src/Laraue.Boards.Agent` - the runner; namespaces follow folders:
  - `Commands` - one class per command (`RunCommand`, `Setup`, `ContinueCommand` + `ContinueSession`,
    `ReviewCommentsCommand`, `Service`) and `RunnerArgs`. `Program.cs` only parses arguments and dispatches.
  - `Runs` - `Runner` (claim -> work -> finish), `ActiveRun` (the agent process, its session, the branches),
    `ReadyRunPoller` (which Ready run to take), `RunsInProgress` (the record that lets a restart resume a run).
  - `Agents` - `ICodingAgent`, `ClaudeCode/`, `Codex/`, `AgentProcess` (the process) and `AgentOutput` (parsing its
    stdout, one class per agent), `CodingAgents.Prompt`, `AgentPlugin` (writes out the embedded skill).
  - `Workspaces` - `WorkspaceManager` (create/finish worktrees and checkouts), `Checkouts`, `SpaceResolver` +
    `CloneIndex` (a space's links matched to local clones by git remote), `RepoUrl`.
  - `Publishing` - `Publisher` (push, `gh pr create`), `PullRequestReviews` (review comments for re-runs).
  - `Boards` - `IBoardsClient`/`BoardsClient` (MCP), the DTOs, `RunnerJson`, sign-in and the credential store.
  - `Config` - `RunnerConfig` and its value sets (`PublishModes`, `WorkspaceModes`, `ReviewCommentLevels`).
  - `Infrastructure` - `Git`, `Shell`, `Notifications`, `Log`, `ConsolePrompt`, `UserPrompt`, Windows helpers.
  - `plugin/` - the `work-on-issue` skill, embedded and passed to Claude Code as a plugin directory (Codex reads the
    same `SKILL.md`). `assets/icon.png` - the notification app's icon, embedded.
- `tests/Laraue.Boards.Agent.Tests` - the only test project.

One type per file, named after it; a small record used only with one type stays in that type's file
(`Workspace` + `WorkspaceRepo`, the Boards DTOs in `BoardsModels.cs`).

## Build, test, release

- `dotnet build boards-agent.slnx`, `dotnet test boards-agent.slnx`. The tests need only git.
- Run from source: `dotnet run --project src/Laraue.Boards.Agent -- <command>`. Against a local Boards:
  `setup --web-url http://localhost:3000 --api-url http://localhost:5200 --mcp-url http://localhost:5202/mcp`.
- Releases are trimmed self-contained single files. **JSON is source-generated only** (reflection is off): every type
  read or written as JSON needs a `[JsonSerializable]` on `RunnerJson`, or a trimmed build fails at run time while
  `dotnet run` works. Check a new DTO with a published build, not only `dotnet run`.
- A `v*` tag on main (`v0.4.0-beta.9` makes a pre-release) runs the tests, then builds the binaries
  (`.github/workflows/release.yml`). The version comes from the tag (`-p:Version`). Never push a tag or a branch
  without the user asking.

## Safety invariants

The runner works unattended on someone's computer, with their API key. Keep these:

- **The agent never pushes or talks to GitHub.** `git push` and `gh` are denied to it (`ClaudeCodeAgent.DisallowedTools`;
  Codex's sandbox). Pushing and pull requests are the runner's (`Publisher`), and only when `publish` says so.
- **Text from other people is data, not instructions**: other people's issue comments, PR review comments, space
  descriptions and links. Prompts quote them as such (see `CodingAgents.Prompt`), and the skill says whose
  instructions count. PR review comments reach the agent only from authors the person trusts
  (`ReviewCommentLevels`, set per repository with `lba review-comments`). That setting stays on the computer, never in
  Boards, so nobody else can raise it.
- **The person's uncommitted changes are never touched.** Checkout mode asks before switching a clone that has any,
  and refuses rather than carrying them over.
- **A run is never left Working by mistake** - that keeps the issue locked. Reporting at the end (comments,
  `finish_agent_run`, cancelling) uses `CancellationToken.None`. The runner being stopped is the one exception: the
  run stays Working on purpose and `RunsInProgress` lets the next start resume it (`Runner.RecoverInterruptedRun`).
- **No real API key in a committed file.** The key lives in the OS credential store (`CredentialStore`), or
  `BOARDS_AGENT_API_KEY`.

## Gotchas

- **Count an agent branch's commits on the branch, not HEAD** (`Git.CommitsSince(repo, base, branch)`). In checkout
  mode `WorkspaceManager.Finish` switches an unchanged clone back to the person's branch and deletes the empty agent
  branch; anything counting HEAD afterwards sees the person's commits.
- **`RunnerConfig` properties are settable, not `init`**: the source generator would set a missing `init` property to
  null/0, so an older config file would lose the default. A new setting also goes in `boards-agent.example.json`, the
  README, `RunnerConfig.Load`'s validation if it has a fixed set of values, and setup if the person should choose it.
  Rerunning setup keeps the answers already given.
- The service reads its config at start; `lba setup` restarts it. A setting changed by another command while it
  runs is read from the file when used (e.g. review comment levels in `Runner.CreateWorkspace`).
- **Windows paths are written but barely tested** (Task Scheduler, `lba.cmd`, Credential Manager). Say so when a
  change touches them.

## Writing tests

- **Public API only.** No `InternalsVisibleTo`, no reflection into private members: the runner's types are public,
  and a test goes through the same entry points as the code that uses it. Something hard to test that way is a sign to
  split it out (e.g. `AgentOutput` parses an agent's stdout without a process; `Runner.PickUpComment` is a pure
  function).
- xunit + Moq, as in the backend repositories. Mock `IBoardsClient` and `ICodingAgent`; don't start a real agent.
- Git behaviour is tested against real git in temp folders: `TestGit` makes a bare `origin` with one commit and a
  clone of it. Never touch the person's own clones, config or credential store from a test.
- Test configs set `Notifications = false`, or the tests pop up real notifications.
- Naming: `{Method}_Should{ExpectedBehavior}_When{Condition}`. Named records, not tuples, for setup results and
  expected values.

## Workflow

- Build bigger changes step by step (behaviour, then tests, then docs), stopping after each step for the user's review.
- Branch per task: `feature/<description>` or `fix/<description>` (`feature/BRD-317-...` when there's an issue).
- Write code like the surrounding code: XML doc comments that say why, early returns, long methods split into named
  steps, no tuples in public signatures.
