# Aegis

> This repository is still named `harness-cli` during the migration. The command and product identity are now Aegis; the repo rename is a later step.

Aegis is a small .NET helper for deterministic delegated agent sessions and durable coordination state across supported backends. It gives agents a stable way to launch delegated sessions, enforce a final handoff summary contract, fetch summaries without loading full transcripts, and keep lightweight recursive cell records for multi-agent coordination.

## Layout

- `src/HarnessCli.Core/`: shared contracts, session registry infrastructure, state normalization, prompt rendering, and cell records.
- `src/HarnessCli.Backends/`: OpenCode, Codex, Pi, and GitHub Copilot CLI backend adapters.
- `src/HarnessCli/`: Aegis CLI application source.
- `src/HarnessCli/WorkMapUi/`: optional Aegis cell observer UI.
- `tests/HarnessCli.UnitTests/`: unit tests.
- `tests/HarnessCli.IntegrationTests/`: integration tests.
- `prompts/`: markdown source files for built-in agent prompts.

Conventions and coding standards are documented in [CONTRIBUTING.md](./CONTRIBUTING.md).

## Build

The solution and package IDs intentionally remain `harness-cli` / `HarnessCli.*` until the repo rename is planned separately.

```powershell
dotnet build harness-cli.sln
dotnet test harness-cli.sln
dotnet publish src/HarnessCli/HarnessCli.csproj -c Release -o "$HOME\.local\bin" --self-contained false
aegis self-test
```

The CLI targets .NET 10 to match the repo test projects and current agent workstation runtime.

Live backend verification is documented in [docs/live-backend-smoke.md](./docs/live-backend-smoke.md). Backend support is considered verified only after a real `ask` reaches the backend and extracts a fresh `FINAL HANDOFF`.

Library/package consumption notes for in-process callers are in [docs/package-consumption.md](./docs/package-consumption.md).

## Install And Compatibility

The workspace installer publishes versioned `aegis.exe` builds under `$HOME\.local\aegis\versions` and installs a primary PATH shim at `$HOME\.local\aegis\bin\aegis.cmd`.

Compatibility aliases remain during migration:

- `harness-cli` forwards to Aegis.
- `opencode-harness-cli` forwards to Aegis.
- `aegis work-map` and `harness-cli work-map` are accepted legacy forms for `aegis cell`.

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

## Delegated Tasks

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

## Fan-Out

Use `spawn` to launch multiple implementation sessions without hand-rolling OpenCode API loops:

```powershell
aegis spawn --model github-copilot/gpt-5.5 --directory "C:\path\to\repo" --target "issue #5" --target "issue #4"
aegis spawn --model github-copilot/gpt-5.5 --directory "C:\path\to\repo" --target "issue #5" --resume-session "issue #5=ses_..."
aegis latest --search "Ship:" --all --limit 20
```

## Cells

Use `cell` when a coordinator needs durable state for a recursive coordination graph: cells, child cells, workstreams, roles, clones, sessions, evidence, final handoffs, blockers, and integration notes.

Records are stored outside target repos by default under `AEGIS_CELL_DIR`, or the platform app-data `aegis/cells` directory when the variable is unset. The legacy `HARNESS_CLI_WORK_MAP_DIR` alias and `harness-cli/work-map` fallback are still accepted.

Create a cell, attach clone-backed workstreams, fork child cells when a worker needs to split work further, fan out worker sessions, and render an optional observer view:

```powershell
aegis cell create --title "Ship search fixes" --intent "Coordinate independent repo slices"
aegis cell stream add --cell cell-... --name "API slice" --role implementer --clone E:\agents\workspaces\api-search-fix
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

For Tailscale Serve without changing firewall rules:

```powershell
aegis cell serve --host 127.0.0.1 --port 4896 --access-log .\cell-access.jsonl
tailscale serve --bg http://127.0.0.1:4896/
```

Cell records use record-level locked mutations and atomic file replacement so multiple worker processes can add streams, sessions, evidence, and child cells to the same parent cell without clobbering each other.

`cell` uses clone/clone-path terminology deliberately. It records detached full task clones; it does not create or require git worktrees.

## Useful Commands

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

## Backend Support

| Backend | Status |
|---|---|
| `opencode` | Default backend with full command coverage. |
| `codex` | Local command-path adapter with session-local state files and JSON message extraction. |
| `pi` | Local command-path adapter using JSON event output and message reconstruction. |
| `copilot` | Conservative blocking non-interactive GitHub Copilot CLI adapter; native async/resume is not claimed yet. |

## Migration Notes

- Default backend remains `opencode`; existing invocation patterns do not change.
- `--backend` accepts `opencode`, `codex`, `pi`, and `copilot`.
- Codex, Pi, and Copilot adapters use local persistent backend state outside the target repository under app data or `AEGIS_BACKEND_STATE_DIR`; `HARNESS_CLI_BACKEND_STATE_DIR` remains a legacy alias.
- `AEGIS_SESSION_DIR` overrides the session registry directory; `HARNESS_CLI_SESSION_DIR` remains a legacy alias.
- `AEGIS_CODEX_BINARY` and `AEGIS_COPILOT_BINARY` override backend executable paths; the old `HARNESS_CLI_*` binary aliases remain accepted.
- See [docs/multi-backend-rollout.md](./docs/multi-backend-rollout.md) for staged parity status and rollout checklist.

## Notes

- Default server is `http://127.0.0.1:4096`.
- Prefer `--model github-copilot/gpt-5.4-mini --variant low` for fast delegated work.
- Use `--model github-copilot/gpt-5.5 --variant high` or `--variant xhigh` only when the delegated task is small but hard.
- `--reasoning` is accepted as an alias for `--variant` when thinking in GPT-5 terms.
- `--raw` disables the handoff wrapper and sends the prompt exactly as provided.
- `watch` and `watch-many` send supervision prompts and support `--until-idle`, `--max-runs`, and `--max-duration-minutes` so supervisors do not need brittle shell loops.
