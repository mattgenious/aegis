# Live Backend Smoke Verification

This project is an agent-facing CLI, so backend support is not considered verified by compilation alone. A backend smoke pass means `harness-cli ask` reached a live backend process, received an assistant response, and extracted a fresh `FINAL HANDOFF` summary.

Last verified: 2026-06-01.

## Source Of Truth Checked

- OpenCode: `opencode --help`, `opencode serve --help`, npm package metadata for `opencode-ai@1.15.10` and `opencode-linux-x64@1.15.10`.
- Codex: local `codex exec --help`; smoke host reported `codex-cli 0.133.0-alpha.1`.
- Pi: local `pi --help` / `pi --mode json --help`; smoke host reported `pi 0.75.4`.
- GitHub Copilot CLI: `copilot --help`; live smoke host resolved `copilot.exe` from the WinGet Links directory.

## Verified Commands

The smoke run used an isolated registry:

```bash
export HARNESS_CLI_SESSION_DIR=/tmp/harness-cli-live-smoke/sessions
```

Use `harness-cli` or `harness-cli.dll` for live smoke commands. The legacy `opencode-harness-cli` name is only a command-shim compatibility alias; direct `dotnet .../opencode-harness-cli.dll` invocation is not part of the migration contract.

OpenCode 1.15.10:

```bash
# The WSL PATH had a Windows npm shim for opencode, so this run used the
# current linux x64 package binary in /tmp/harness-cli-live-smoke/bin.
PATH="/tmp/harness-cli-live-smoke/bin:$PATH" \
  opencode serve --hostname 0.0.0.0 --port 4096 --print-logs --log-level DEBUG

dotnet src/HarnessCli/bin/Debug/net10.0/harness-cli.dll ask \
  --server http://127.0.0.1:4096 \
  --directory /tmp/harness-cli-live-smoke/repo \
  --timeout 240 \
  --model opencode/deepseek-v4-flash-free \
  --title "Smoke opencode live available model" \
  --prompt "Smoke test. Do not run tools. Reply exactly with: FINAL HANDOFF
opencode backend smoke passed"
```

Result: `summary = "opencode backend smoke passed"`.

Codex:

```bash
dotnet src/HarnessCli/bin/Debug/net10.0/harness-cli.dll ask \
  --backend codex \
  --directory /tmp/harness-cli-live-smoke/repo \
  --timeout 180 \
  --prompt "Smoke test. Do not run tools. Reply exactly with: FINAL HANDOFF
codex backend smoke passed"
```

Result: `summary = "codex backend smoke passed"`.

Pi:

```bash
dotnet src/HarnessCli/bin/Debug/net10.0/harness-cli.dll ask \
  --backend pi \
  --directory /tmp/harness-cli-live-smoke/repo \
  --timeout 240 \
  --prompt "Smoke test. Do not run tools. Reply exactly with: FINAL HANDOFF
pi backend smoke passed"
```

Result: `summary = "pi backend smoke passed"`.

GitHub Copilot CLI:

```bash
dotnet src/HarnessCli/bin/Debug/net10.0/harness-cli.dll ask \
  --backend copilot \
  --directory /tmp/harness-cli-live-smoke/repo \
  --timeout 240 \
  --prompt "Smoke test. Do not run tools. Reply exactly with: FINAL HANDOFF
copilot backend smoke passed"
```

Result: `summary = "copilot backend smoke passed"` through `harness-cli work-map session run --backend copilot --raw`; the work-map record reached `status = "handoff"` with a fresh assistant summary.

## Live Fixes From This Pass

2026-06-01:

- Added GitHub Copilot CLI backend support behind `--backend copilot`; initially blocked on missing local `copilot` binary.
- Reverified GitHub Copilot CLI after installing it with WinGet; `copilot.exe` resolved on PATH and `work-map session run --backend copilot --raw` extracted `FINAL HANDOFF` successfully.

2026-05-23:

- Published `HarnessCli.Core 0.1.0` and `HarnessCli.Backends 0.1.0` to GitHub Packages and validated Baton can restore/use the package-backed launcher path.
- Published `HarnessCli.Core 0.1.1` and `HarnessCli.Backends 0.1.1` after moving Codex/Pi backend state outside target repositories.
- Reverified live `ask` flows against OpenCode 1.15.10, Codex CLI 0.133.0-alpha.1, and Pi 0.75.4.
- The WSL `opencode` command resolved to a Windows npm shim that could not run the Linux binary. The smoke used the current `opencode-linux-x64@1.15.10` package binary directly.
- `ensure-server --print-logs` reached a healthy OpenCode 1.15.10 server but stayed attached to log streaming in this shell. The live OpenCode smoke used a directly started server while preserving the same HTTP `ask` path.

2026-05-22:

- The CLI app now targets .NET 10, matching the available agent workstation runtime and the test projects.
- Unix `ensure-server` now leaves OpenCode running with persistent inert stdin; OpenCode 1.15.7 exits shortly after stdin closes.
- Codex uses current exec flags: `--json`, `--dangerously-bypass-approvals-and-sandbox`, `--skip-git-repo-check`, and `--cd`.
- Codex parsing handles current `item.completed` / `agent_message` JSONL events.
- Pi uses current non-interactive flags: `--mode json --print`.
- Pi parsing handles current `message_end` / `turn_end` events with text in `message.content[]`.
