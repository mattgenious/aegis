# Harness CLI

> Repository now uses a `src/` + `tests/` structure before backend abstraction work.

## Layout

- `src/HarnessCli.Core/` – reusable contracts, session registry infrastructure, state normalization, and prompt rendering
- `src/HarnessCli.Backends/` – reusable OpenCode, Codex, and Pi backend adapters
- `src/HarnessCli/` – CLI application source
- `tests/HarnessCli.UnitTests/` – unit tests
- `tests/HarnessCli.IntegrationTests/` – integration tests
- `prompts/` – markdown source files for built-in agent prompts

Conventions and coding standards are documented in [CONTRIBUTING.md](./CONTRIBUTING.md).

Small .NET helper for deterministic delegated agent sessions and durable coordination state across supported backends.

The goal is not to wrap every backend endpoint. It gives agents a stable, low-friction way to launch delegated sessions, enforce a final handoff summary contract, fetch that summary without loading the whole session into context, and keep lightweight work-map records for multi-agent coordination.

## Build

```powershell
dotnet build harness-cli.sln
dotnet test harness-cli.sln
dotnet publish src/HarnessCli/HarnessCli.csproj -c Release -o "$HOME\.local\bin" --self-contained false
harness-cli self-test
```

The CLI targets .NET 10 to match the repo test projects and current agent workstation runtime.

Live backend verification is documented in [docs/live-backend-smoke.md](./docs/live-backend-smoke.md). Backend support is considered verified only after a real `ask` reaches the backend and extracts a fresh `FINAL HANDOFF`.

Library/package consumption notes for Baton and other callers are in [docs/package-consumption.md](./docs/package-consumption.md).

> Legacy `scripts/opencode-harness-cli` path references in old docs are preserved for historical context only.

The installer publishes versioned `harness-cli.exe` builds under `$HOME\.local\harness-cli\versions` and installs a primary PATH shim at `$HOME\.local\harness-cli\bin\harness-cli.cmd`. It also keeps `opencode-harness-cli` as a compatibility alias for existing scripts during the transition. Open a new terminal after install so the higher-priority shim is used instead of any older locked executable.

## Help

Every command supports `-h`, `--help`, and `help <command>`:

```powershell
harness-cli --help
harness-cli watch -h
harness-cli watch --help
harness-cli help watch
```

During the migration window, `opencode-harness-cli` forwards to the same command when installed by the workspace plugin or produced by `dotnet publish`.

## Server

Start or verify a local unauthenticated OpenCode server:

```powershell
harness-cli ensure-server --hostname 0.0.0.0 --port 4096 --print-logs
```

`ensure-server` removes `OPENCODE_SERVER_PASSWORD` and `OPENCODE_SERVER_USERNAME` from the child process so inherited shell auth settings do not accidentally force HTTP Basic auth.

If OpenCode logs that the server is listening before `/global/health` responds, `ensure-server` returns `started-listening` instead of waiting indefinitely. This keeps terminal wrappers from killing the child server during startup.

This matters because raw `opencode run` can fail in some environments when those same variables are exported in the parent shell. In that case the embedded `run` self-start or self-attach path can return a misleading `Session not found` or `401 Unauthorized` before any model call is made.

Preferred automation flow:

1. Use `ensure-server` to start or reuse a local server for API-based automation.
2. Point harness commands at that server.
3. If you are troubleshooting plain OpenCode CLI behavior, start `opencode serve` separately and use `opencode run --attach http://127.0.0.1:4096 ...` instead of relying on `opencode run` to bootstrap its own server.

## Delegated Task

Run a task in a new OpenCode session and extract the final handoff summary:

```powershell
harness-cli ask --model github-copilot/gpt-5.4-mini --variant low --title "Check API docs" --prompt "Read the local API docs and summarize the session endpoints."
```

For longer tasks, the default path queues the prompt asynchronously and polls status/messages until the final handoff appears. It does not wait on OpenCode's model response stream:

```powershell
harness-cli ask --timeout 900 --model github-copilot/gpt-5.4-mini --variant low --prompt-file task.md
```

Use `--async` when you want to return immediately and fetch the summary later:

```powershell
harness-cli ask --async --model github-copilot/gpt-5.4-mini --variant low --prompt-file task.md
```

The CLI wraps prompts with a handoff contract. The delegated agent is told to put the final answer under this exact marker:

```text
FINAL HANDOFF
```

`last-summary` returns only the final assistant text after that marker, anchored after the latest user prompt so older historical handoffs are not mistaken for current progress:

```powershell
harness-cli last-summary --session ses_... --plain
```

## Fan-Out Helper

Use `spawn` to launch multiple implementation sessions without hand-rolling OpenCode API loops:

```powershell
harness-cli spawn --model github-copilot/gpt-5.5 --directory "C:\path\to\repo" --target "issue #5" --target "issue #4"
```

The command queues each target asynchronously and prints target/session/status JSON for later inspection with `status` and `last-summary`. Add `--wait` when the coordinator should block until each worker returns a `FINAL HANDOFF` summary; without `--wait`, `spawn` only proves the prompt was queued.

If a target was already launched, resume it instead of creating a duplicate:

```powershell
harness-cli spawn --model github-copilot/gpt-5.5 --directory "C:\path\to\repo" --target "issue #5" --resume-session "issue #5=ses_..."
```

Use `latest --all` to inspect every matching session title instead of only the newest one:

```powershell
harness-cli latest --search "Ship:" --all --limit 20
```

## Work Map

Use `work-map` when a coordinator needs durable state for a mission graph: workstreams, roles, clones, sessions, evidence, final handoffs, blockers, and integration notes. Records are stored outside target repos by default under `HARNESS_CLI_WORK_MAP_DIR`, or the platform app-data `harness-cli/work-map` directory when the variable is unset.

Create a mission, attach a clone-backed workstream, run or link a session, and render an optional observer view:

```powershell
harness-cli work-map create --title "Ship search fixes" --intent "Coordinate independent repo slices"
harness-cli work-map stream add --mission mission-... --name "API slice" --role implementer --clone E:\agents\workspaces\api-search-fix
harness-cli work-map session run --mission mission-... --stream stream-... --backend codex --directory E:\agents\workspaces\api-search-fix --prompt-file task.md
harness-cli work-map show --mission mission-... --format html --output work-map.html
```

The HTML output is a static optional observer over the same JSON records. It is not required for harness-cli execution and does not need a server.

`work-map` uses clone/clone-path terminology deliberately. It records detached full task clones; it does not create or require git worktrees.

## Useful Commands

```powershell
harness-cli health
harness-cli self-test
harness-cli new --title "scratch"
harness-cli spawn --target "issue #5" --target "issue #4" --model github-copilot/gpt-5.5
harness-cli latest --search "Check API docs"
harness-cli status
harness-cli status --session ses_...
harness-cli wait --session ses_...
harness-cli messages --session ses_... --limit 20
harness-cli tail --session ses_... --limit 20 --once
harness-cli events --limit 10 --timeout 30
harness-cli abort --session ses_...
harness-cli export --session ses_... --format md --output session-export.md
harness-cli work-map show --mission mission-... --format md
```

## Backend Support Matrix

The CLI now includes backend adapters for:

- `opencode` (default): existing OpenCode HTTP API path with full command coverage (`ask`, `spawn`, `status`, `wait`, `watch`, `tail`, etc.).
- `codex`: local command-path adapter (`codex`) with explicit session-local state files and JSON message extraction.
- `pi`: local command-path adapter (`pi`) using JSON event output (`--mode json`) and message reconstruction.

Current runtime support status:

| Command | opencode | codex | pi |
|---|---|---|---|
| `new` | ✅ | ✅ | ✅ |
| `latest` | ✅ | ✅ | ✅ |
| `ask` | ✅ | ✅ | ✅ |
| `messages` | ✅ | ✅ | ✅ |
| `wait` | ✅ | ✅ | ✅ |
| `last-summary` | ✅ | ✅ | ✅ |
| `status` | ✅ | ✅ | ✅ |
| `abort` | ✅ | ✅ | ✅ |

Legend: ✅ command fully wired in this release, ⚙️ adapter exists and is tested but full command wiring is the next integration step.

## Migration Notes

- Default backend remains `opencode`; existing invocation patterns do not change.
- `--backend` currently accepts `opencode`, `codex`, and `pi` and is validated at parse time.
- OpenCode semantics remain the compatibility baseline for prompt wrapping, handoff markers, and summary extraction behavior.
- Codex and Pi adapters use local persistent backend state outside the target repository by default, under the platform app data directory or `HARNESS_CLI_BACKEND_STATE_DIR` when set. This avoids dirtying the repo and prevents delegated agents from deleting their own session transcript.
- Work-map mission/session history uses a separate provider-neutral store under `HARNESS_CLI_WORK_MAP_DIR` or app data `harness-cli/work-map`. This store is safe for optional observer UIs and future SQLite migration without changing the conceptual model.
- Use `--raw` when you want a backend to receive prompt text verbatim.
- See [docs/multi-backend-rollout.md](./docs/multi-backend-rollout.md) for staged parity status and rollout checklist.

## Safe Probe

Use `--no-reply` to write a user message without calling a model:

```powershell
harness-cli ask --no-reply --prompt "Context-only probe."
```

## Session Watch

Send a prompt to an existing OpenCode session immediately, then repeat it on an interval:

```powershell
harness-cli watch --session ses_... --directory "C:\path\to\workspace" --interval-minutes 15 --prompt "Check progress and continue safe shipping work."
```

`watch` sends the prompt exactly as provided. It does not add the delegated pseudo-subagent handoff wrapper used by `ask`. Use `--prompt-file` for longer recurring supervision prompts.

`wait` is the passive way to block until existing work becomes idle. It sends no prompt and deliberately has no timeout option; press Ctrl+C to stop waiting.

```powershell
harness-cli wait --session ses_...
```

Stop active supervision automatically when the session is idle, after a maximum number of rounds, or after a maximum duration:

```powershell
harness-cli watch --session ses_... --until-idle --max-runs 12 --interval-minutes 10
harness-cli watch --session ses_... --max-duration-minutes 120 --interval-minutes 15
```

Watch several sessions from one supervisor process by repeating `--session`:

```powershell
harness-cli watch-many --session ses_a --session ses_b --until-idle --interval-minutes 10 --prompt-file watch-prompt.md
```

## Tail And Export

Use `tail` for a compact polling view of recent text messages. Add `--once` for a one-shot snapshot, or omit it to keep polling:

```powershell
harness-cli tail --session ses_... --limit 20 --interval-seconds 5
```

Use `export` to save status, final handoff summary, and messages as JSON or Markdown:

```powershell
harness-cli export --session ses_... --format json --output session.json
harness-cli export --session ses_... --format md --output session.md
```

## Notes

- Default server is `http://127.0.0.1:4096`.
- Prefer `--model github-copilot/gpt-5.4-mini --variant low` for fast delegated work.
- Use `--model github-copilot/gpt-5.5 --variant high` or `--variant xhigh` only when the delegated task is small but hard.
- `--model` must use `provider/model` format because OpenCode's `/session/{id}/message` API expects separate `providerID` and `modelID` fields.
- OpenCode calls reasoning effort a model `variant`. For GitHub Copilot GPT-5-family models, verified local variants are `none`, `low`, `medium`, `high`, and `xhigh`.
- `--reasoning` is accepted as an alias for `--variant` when thinking in GPT-5 terms.
- `--raw` disables the handoff wrapper and sends the prompt exactly as provided.
- Real model prompts use OpenCode's `/prompt_async` endpoint plus polling, so the CLI does not hang on streaming model-response endpoints.
- `--async` means return immediately after queueing; omit it for queue-and-wait behavior.
- `messages` defaults to `--limit 20`; `events` defaults to `--limit 10 --timeout 30`.
- `wait --session` passively waits until OpenCode reports the session idle. It does not send prompts and does not accept `--timeout`.
- `watch` and `watch-many` send supervision prompts and support `--until-idle`, `--max-runs`, and `--max-duration-minutes` so supervisors do not need brittle shell loops.
- `last-summary` ignores historical handoffs before the latest prompt round; if no fresh handoff exists yet it fails instead of returning stale progress.
- `watch --until-idle` stops after its prompted supervision run becomes idle; it is not passive waiting. OpenCode omits idle sessions from `/session/status`, so a missing status entry counts as idle.
- `tail` gives a compact live view; `export` creates durable JSON or Markdown handoffs.
- The installed OpenCode `1.14.33` `/doc` endpoint can be incomplete. This tool targets the verified session endpoints used by the generated SDK and live API.
- If the harness reports `401 Unauthorized`, the target OpenCode server is probably auth-protected. Either start a fresh unauthenticated child server with `ensure-server` or attach to a server you started intentionally.
