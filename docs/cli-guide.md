# Aegis CLI Guide

This guide keeps the command-heavy reference material out of the root README. Start with the README if you only need the product overview and quick start.

## Build

The solution and package IDs are `aegis` / `Aegis.*`.

```powershell
dotnet build aegis.sln
dotnet test aegis.sln
dotnet publish src/Aegis/Aegis.csproj -c Release -o "$HOME\.local\bin" --self-contained false
aegis self-test
```

The CLI targets .NET 10 to match the repo test projects and current agent workstation runtime.

`dotnet publish` builds the optional React cell observer UI from `src/Aegis/CellUi` and includes the generated bundle in the publish output. Pass `-p:BuildCellUiOnPublish=false` to skip the UI bundle; `aegis cell serve` will then show a local fallback page with build instructions.

Live backend verification is documented in [live-backend-smoke.md](./live-backend-smoke.md). Backend support is considered verified only after a real `ask` reaches the backend and extracts a fresh `FINAL HANDOFF`.

Before choosing a delegated backend, run:

```powershell
aegis backend detect
```

Aegis reports local command availability in this priority order: Codex, OpenCode, Pi, then standalone GitHub Copilot CLI. Detection checks the local command surface only; authentication, model access, and OpenCode server health still require a live backend smoke. Cell launch/session-run uses the first available backend in that order only when no backend/profile/model controls are provided; explicit `--backend` always wins.

Library/package consumption notes for in-process callers are in [package-consumption.md](./package-consumption.md).

## Install and compatibility

Aegis can be installed directly from this repository:

```powershell
git clone https://github.com/mattgenious/aegis.git
cd aegis
powershell -File scripts/install-aegis.ps1
```

The standalone installer publishes versioned `aegis.exe` builds under `$HOME\.local\aegis\versions`, installs a primary PATH shim at `$HOME\.local\aegis\bin\aegis.cmd`, updates the active marker, and prunes old versions by default. Use `-DryRun` to preview and `-InstallRoot <path>` to target a different install directory.

To install the Aegis-owned VS Code Copilot Chat support templates from this repo, run one of:

```powershell
powershell -File scripts/install-vscode.ps1
powershell -File scripts/install-aegis.ps1 -InstallVSCodeSupport
```

```sh
sh scripts/install-vscode.sh
```

The VS Code support installer copies only the Aegis templates from `support/vscode/` into `$HOME/.copilot/agents`, `$HOME/.copilot/instructions`, and `$HOME/.copilot/prompts`. Use `-DryRun` on PowerShell or `--dry-run` on POSIX shells to preview. Use `-TargetRoot <path>` / `--target-root <path>` or `-ProfileRoot <path>` / `--profile-root <path>` to install into a test directory or a non-default Copilot profile root. Use `-WorkspaceRoot <path>` / `--workspace-root <path>` to also install workspace-scoped copies under `.github/`.

VS Code support is terminal/tool based: Aegis can be launched from VS Code and can spawn or supervise external backend sessions through OpenCode, Codex, GitHub Copilot CLI, or other supported Aegis backends. It does not drive VS Code's native Copilot Chat UI as a backend session host.

Compatibility aliases:

- `aegis` is the primary command.
- `opencode-aegis` remains a migration alias.
- `work-map` is a legacy command form that still routes to `cell` during transition.

Open a new terminal after install so the higher-priority shim is used instead of any older locked executable.

## Help

Every command supports `-h`, `--help`, and `help <command>`:

```powershell
aegis --help
aegis watch -h
aegis watch --help
aegis help watch
aegis help cell
```

## Server

Start or verify a local unauthenticated OpenCode server:

```powershell
aegis ensure-server --hostname 0.0.0.0 --port 4096 --print-logs
```

`ensure-server` removes `OPENCODE_SERVER_PASSWORD` and `OPENCODE_SERVER_USERNAME` from the child process so inherited shell auth settings do not accidentally force HTTP Basic auth.

## Delegated tasks

Run a task in a new backend session and extract the final handoff summary:

```powershell
aegis ask --model github-copilot/gpt-5.4-mini --variant low --title "Check API docs" --prompt "Read the local API docs and summarize the session endpoints."
```

For longer tasks, the default path queues the prompt asynchronously and polls status/messages until the final handoff appears:

```powershell
aegis ask --timeout 900 --model github-copilot/gpt-5.4-mini --variant low --prompt-file task.md
```

Use `--async` when you want to return immediately and fetch the summary later:

```powershell
aegis ask --async --model github-copilot/gpt-5.4-mini --variant low --prompt-file task.md
aegis last-summary --session ses_... --plain
```

## Fan-out

Use `spawn` to launch multiple implementation sessions without hand-rolling OpenCode API loops:

```powershell
aegis spawn --model github-copilot/gpt-5.5 --directory "C:\path\to\repo" --target "issue #5" --target "issue #4"
aegis spawn --model github-copilot/gpt-5.5 --directory "C:\path\to\repo" --target "issue #5" --resume-session "issue #5=ses_..."
aegis latest --search "Ship:" --all --limit 20
```

## Cells

Use `cell` when a coordinator needs durable state for a recursive coordination graph: cells, child cells, workstreams, roles, clones, sessions, evidence, final handoffs, blockers, and integration notes.

Records are stored outside target repos by default under `AEGIS_CELL_DIR`, or the platform app-data `aegis/cells` directory when the variable is unset. The legacy `HARNESS_CLI_WORK_MAP_DIR` alias and `aegis work-map` fallback command form are still accepted.

Create a cell, attach clone-backed workstreams, fork child cells when a worker needs to split work further, fan out worker sessions, and render an optional observer view:

```powershell
aegis cell create --title "Ship search fixes" --intent "Coordinate independent repo slices"
aegis cell stream add --cell cell-... --name "API slice" --role implementer --clone C:\workspaces\api-search-fix
aegis cell fork --cell cell-... --title "Search indexing slice" --intent "Let a worker recursively split indexing work"
aegis cell launch --cell cell-... --backend codex --prompt-file worker-context.md
aegis cell session run --cell cell-... --stream stream-... --backend copilot --prompt-file worker-context.md
aegis cell show --cell cell-... --format html --output cell.html
aegis cell serve --host 127.0.0.1 --port 4896 --access-log .\cell-access.jsonl
```

Keep the cell current as workers report back:

```powershell
aegis cell update --cell cell-... --status in-progress --next-action "Review worker handoffs"
aegis cell stream update --cell cell-... --stream stream-... --status needs-review --integration-action "Cherry-pick patch and run tests"
aegis cell supervise --cell cell-... --launch-missing --max-runs 1
aegis cell session sync --cell cell-... --all
aegis cell store export --output cell-snapshot.json
aegis cell session handoff --session codex-... --summary "Patch is ready; tests passed."
aegis cell session blocker set --session codex-... --summary "Cannot run tests: SDK missing" --evidence "dotnet test failed before restore"
aegis cell session verify --session codex-... --kind parent-review --result pass --summary "Diff and tests checked by coordinator."
```

The observer UI is optional and read-only. It polls the same JSON records, writes request lines to stderr, and can append durable JSONL access records with `--access-log FILE`.

Cell coordination is velocity-first: when uncertainty, missing evidence, independent slices, or competing hypotheses exist, fan out worker streams early, require comparison-ready handoffs, verify in parallel where practical, then consolidate, prune, and redirect from evidence.

When a backend session has stopped without a fresh final handoff, `cell session sync` records it as `needs-restart-or-nudge` so coordinators can restart or nudge it without mistaking it for a blocker.

For Tailscale Serve without changing firewall rules:

```powershell
aegis cell serve --host 127.0.0.1 --port 4896 --access-log .\cell-access.jsonl
tailscale serve --bg http://127.0.0.1:4896/
```

Cell records use record-level locked mutations and atomic file replacement so multiple worker processes can add streams, sessions, evidence, and child cells to the same parent cell without clobbering each other.

`cell` uses clone/clone-path terminology deliberately. It records detached full task clones; it does not create or require git worktrees.

## Useful commands

```powershell
aegis health
aegis self-test
aegis new --title "scratch"
aegis status --session ses_...
aegis wait --session ses_...
aegis messages --session ses_... --limit 20
aegis tail --session ses_... --limit 20 --once
aegis events --limit 10 --timeout 30
aegis abort --session ses_...
aegis export --session ses_... --format md --output session-export.md
aegis cell show --cell cell-... --format md
aegis cell session sync --cell cell-... --all
```

## Backend support

| Backend | Status |
|---|---|
| `opencode` | Default backend with full command coverage. |
| `codex` | Local command-path adapter with session-local state files and JSON message extraction. |
| `pi` | Local command-path adapter using JSON event output and message reconstruction. |
| `copilot` | Conservative blocking non-interactive GitHub Copilot CLI adapter; native async/resume is not claimed yet. |

## Migration notes

- Default backend remains `opencode`; existing invocation patterns do not change.
- `--backend` accepts `opencode`, `codex`, `pi`, and `copilot`.
- Codex, Pi, and Copilot adapters use local persistent backend state outside the target repository under app data or `AEGIS_BACKEND_STATE_DIR`; `HARNESS_CLI_BACKEND_STATE_DIR` remains a legacy alias.
- `AEGIS_SESSION_DIR` overrides the session registry directory; `HARNESS_CLI_SESSION_DIR` remains a legacy alias.
- `AEGIS_CODEX_BINARY` and `AEGIS_COPILOT_BINARY` override backend executable paths; the old `HARNESS_CLI_*` binary aliases remain accepted.
- See [multi-backend-rollout.md](./multi-backend-rollout.md) for staged parity status and rollout checklist.

## Notes

- Default server is `http://127.0.0.1:4096`.
- Prefer `--model github-copilot/gpt-5.4-mini --variant low` for fast delegated work.
- Use `--model github-copilot/gpt-5.5 --variant high` or `--variant xhigh` only when the delegated task is small but hard.
- `--reasoning` is accepted as an alias for `--variant` when thinking in GPT-5 terms.
- `--raw` disables the handoff wrapper and sends the prompt exactly as provided.
- `watch` and `watch-many` send supervision prompts and support `--until-idle`, `--max-runs`, and `--max-duration-minutes` so supervisors do not need brittle shell loops.
