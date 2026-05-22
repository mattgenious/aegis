# Live Backend Smoke Verification

This project is an agent-facing CLI, so backend support is not considered verified by compilation alone. A backend smoke pass means `harness-cli ask` reached a live backend process, received an assistant response, and extracted a fresh `FINAL HANDOFF` summary.

Last verified: 2026-05-22.

## Source Of Truth Checked

- OpenCode: `opencode --help`, `opencode serve --help`, `opencode models`, npm package metadata for `opencode-ai@1.15.7`.
- Codex: local `codex exec --help` and current upstream `openai/codex` CLI source for exec flags.
- Pi: local `pi --help` / `pi --mode json --help` and live JSON event output.

## Verified Commands

The smoke run used an isolated registry:

```bash
export HARNESS_CLI_SESSION_DIR=/tmp/harness-cli-live-smoke/sessions
```

OpenCode 1.15.7:

```bash
dotnet src/HarnessCli/bin/Debug/net10.0/opencode-harness-cli.dll ensure-server \
  --hostname 0.0.0.0 \
  --port 4096 \
  --directory /tmp/harness-cli-live-smoke/repo \
  --timeout 60 \
  --print-logs

dotnet src/HarnessCli/bin/Debug/net10.0/opencode-harness-cli.dll ask \
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
dotnet src/HarnessCli/bin/Debug/net10.0/opencode-harness-cli.dll ask \
  --backend codex \
  --directory /tmp/harness-cli-live-smoke/repo \
  --timeout 180 \
  --prompt "Smoke test. Do not run tools. Reply exactly with: FINAL HANDOFF
codex backend smoke passed"
```

Result: `summary = "codex backend smoke passed"`.

Pi:

```bash
dotnet src/HarnessCli/bin/Debug/net10.0/opencode-harness-cli.dll ask \
  --backend pi \
  --directory /tmp/harness-cli-live-smoke/repo \
  --timeout 240 \
  --prompt "Smoke test. Do not run tools. Reply exactly with: FINAL HANDOFF
pi backend smoke passed"
```

Result: `summary = "pi backend smoke passed"`.

## Live Fixes From This Pass

- The CLI app now targets .NET 10, matching the available agent workstation runtime and the test projects.
- Unix `ensure-server` now leaves OpenCode running with persistent inert stdin; OpenCode 1.15.7 exits shortly after stdin closes.
- Codex uses current exec flags: `--json`, `--dangerously-bypass-approvals-and-sandbox`, `--skip-git-repo-check`, and `--cd`.
- Codex parsing handles current `item.completed` / `agent_message` JSONL events.
- Pi uses current non-interactive flags: `--mode json --print`.
- Pi parsing handles current `message_end` / `turn_end` events with text in `message.content[]`.
