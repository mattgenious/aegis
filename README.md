# OpenCode Harness CLI

> Repository now uses a `src/` + `tests/` structure before backend abstraction work.

## Layout

- `src/HarnessCli/` – main CLI application source
- `tests/HarnessCli.UnitTests/` – unit tests
- `tests/HarnessCli.IntegrationTests/` – integration tests

Conventions and coding standards are documented in [CONTRIBUTING.md](./CONTRIBUTING.md).

Small .NET helper for deterministic calls to the local OpenCode HTTP API.

The goal is not to wrap every OpenCode endpoint. It gives agents a stable, low-friction way to launch pseudo-subagent sessions on cheaper/faster models, enforce a final handoff summary contract, and fetch that summary without loading the whole session into context.

## Build

```powershell
dotnet build harness-cli.sln
dotnet test harness-cli.sln
dotnet publish src/HarnessCli/OpencodeHarnessCli.csproj -c Release -o "$HOME\.local\bin" --self-contained false
opencode-harness-cli self-test
```

> Legacy `scripts/opencode-harness-cli` path references in old docs are preserved for historical context only.

The OpenCode installer publishes versioned `opencode-harness-cli.exe` builds under `$HOME\.local\opencode-harness-cli\versions` and installs a PATH shim at `$HOME\.local\opencode-harness-cli\bin\opencode-harness-cli.cmd`. Open a new terminal after install so the higher-priority shim is used instead of any older locked executable.

## Help

Every command supports `-h`, `--help`, and `help <command>`:

```powershell
opencode-harness-cli --help
opencode-harness-cli watch -h
opencode-harness-cli watch --help
opencode-harness-cli help watch
```

## Server

Start or verify a local unauthenticated OpenCode server:

```powershell
opencode-harness-cli ensure-server --hostname 0.0.0.0 --port 4096 --print-logs
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
opencode-harness-cli ask --model github-copilot/gpt-5.4-mini --variant low --title "Check API docs" --prompt "Read the local API docs and summarize the session endpoints."
```

For longer tasks, the default path queues the prompt asynchronously and polls status/messages until the final handoff appears. It does not wait on OpenCode's model response stream:

```powershell
opencode-harness-cli ask --timeout 900 --model github-copilot/gpt-5.4-mini --variant low --prompt-file task.md
```

Use `--async` when you want to return immediately and fetch the summary later:

```powershell
opencode-harness-cli ask --async --model github-copilot/gpt-5.4-mini --variant low --prompt-file task.md
```

The CLI wraps prompts with a handoff contract. The delegated agent is told to put the final answer under this exact marker:

```text
FINAL HANDOFF
```

`last-summary` returns only the final assistant text after that marker, anchored after the latest user prompt so older historical handoffs are not mistaken for current progress:

```powershell
opencode-harness-cli last-summary --session ses_... --plain
```

## Fan-Out Helper

Use `spawn` to launch multiple implementation sessions without hand-rolling OpenCode API loops:

```powershell
opencode-harness-cli spawn --model github-copilot/gpt-5.5 --directory "C:\path\to\repo" --target "issue #5" --target "issue #4"
```

The command queues each target asynchronously and prints target/session/status JSON for later inspection with `status` and `last-summary`. Add `--wait` when the coordinator should block until each worker returns a `FINAL HANDOFF` summary; without `--wait`, `spawn` only proves the prompt was queued.

If a target was already launched, resume it instead of creating a duplicate:

```powershell
opencode-harness-cli spawn --model github-copilot/gpt-5.5 --directory "C:\path\to\repo" --target "issue #5" --resume-session "issue #5=ses_..."
```

Use `latest --all` to inspect every matching session title instead of only the newest one:

```powershell
opencode-harness-cli latest --search "Ship:" --all --limit 20
```

## Useful Commands

```powershell
opencode-harness-cli health
opencode-harness-cli self-test
opencode-harness-cli new --title "scratch"
opencode-harness-cli spawn --target "issue #5" --target "issue #4" --model github-copilot/gpt-5.5
opencode-harness-cli latest --search "Check API docs"
opencode-harness-cli status
opencode-harness-cli status --session ses_...
opencode-harness-cli wait --session ses_...
opencode-harness-cli messages --session ses_... --limit 20
opencode-harness-cli tail --session ses_... --limit 20 --once
opencode-harness-cli events --limit 10 --timeout 30
opencode-harness-cli abort --session ses_...
opencode-harness-cli export --session ses_... --format md --output session-export.md
```

## Safe Probe

Use `--no-reply` to write a user message without calling a model:

```powershell
opencode-harness-cli ask --no-reply --prompt "Context-only probe."
```

## Session Watch

Send a prompt to an existing OpenCode session immediately, then repeat it on an interval:

```powershell
opencode-harness-cli watch --session ses_... --directory "C:\path\to\workspace" --interval-minutes 15 --prompt "Check progress and continue safe shipping work."
```

`watch` sends the prompt exactly as provided. It does not add the delegated pseudo-subagent handoff wrapper used by `ask`. Use `--prompt-file` for longer recurring supervision prompts.

`wait` is the passive way to block until existing work becomes idle. It sends no prompt and deliberately has no timeout option; press Ctrl+C to stop waiting.

```powershell
opencode-harness-cli wait --session ses_...
```

Stop active supervision automatically when the session is idle, after a maximum number of rounds, or after a maximum duration:

```powershell
opencode-harness-cli watch --session ses_... --until-idle --max-runs 12 --interval-minutes 10
opencode-harness-cli watch --session ses_... --max-duration-minutes 120 --interval-minutes 15
```

Watch several sessions from one supervisor process by repeating `--session`:

```powershell
opencode-harness-cli watch-many --session ses_a --session ses_b --until-idle --interval-minutes 10 --prompt-file watch-prompt.md
```

## Tail And Export

Use `tail` for a compact polling view of recent text messages. Add `--once` for a one-shot snapshot, or omit it to keep polling:

```powershell
opencode-harness-cli tail --session ses_... --limit 20 --interval-seconds 5
```

Use `export` to save status, final handoff summary, and messages as JSON or Markdown:

```powershell
opencode-harness-cli export --session ses_... --format json --output session.json
opencode-harness-cli export --session ses_... --format md --output session.md
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
