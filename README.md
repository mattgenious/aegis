# Aegis

Aegis is a small .NET CLI for running delegated AI-agent sessions and keeping coordination state durable.

It is meant for people who already use command-line agent tools and want a safer coordination layer around them: consistent session records, final handoff summaries, backend routing, and lightweight multi-agent "cell" state that can survive terminal restarts.

## Why use it?

- **One command surface for multiple agent backends.** Aegis can route work through OpenCode, Codex, Pi, or GitHub Copilot CLI where those tools are installed and authenticated.
- **Handoffs instead of transcript spelunking.** Delegated tasks are wrapped so workers return a `FINAL HANDOFF` summary that can be fetched later with `aegis last-summary`.
- **Durable coordination state.** Cells track streams, clones, sessions, evidence, blockers, verification, child cells, and integration notes in JSON records outside the target repo.
- **Restart-friendly supervision.** Stopped sessions without a final handoff are marked `needs-restart-or-nudge`, not `blocked`, so coordinators can recover them deliberately.
- **CLI-first today, library/server-friendly later.** The current product is a local CLI plus reusable .NET libraries. The boundaries are intentionally backend-oriented so future server integrations, including an MCP server, can build on the same core concepts.

Aegis is not a hosted agent platform and does not replace your backend CLIs. It orchestrates and records work done through tools you run locally.

## Quick start

Prerequisites:

- .NET 10 SDK or runtime for local build/install.
- At least one supported backend CLI if you want live delegated sessions: `opencode`, `codex`, `pi`, or `copilot`.
- Node/npm only if you publish the optional Cell observer UI from source.

Install from this repository:

```powershell
git clone https://github.com/mattgenious/aegis.git
cd aegis
powershell -File scripts/install-aegis.ps1
```

Open a new terminal, then check the install and available backends:

```powershell
aegis self-test
aegis backend detect
```

Run a small delegated task:

```powershell
aegis ask --timeout 300 --prompt "Read the repository layout and return a short FINAL HANDOFF summary."
```

For longer-running work, use `--async` and fetch the summary later:

```powershell
aegis ask --async --title "Check docs" --prompt-file task.md
aegis last-summary --session ses_... --plain
```

## Cells for multi-agent work

Use `aegis cell` when a coordinator needs durable state for several workers or workstreams:

```powershell
aegis cell create --title "Ship search fixes" --intent "Coordinate independent repo slices"
aegis cell stream add --cell cell-... --name "API slice" --role implementer --clone C:\workspaces\api-search-fix
aegis cell launch --cell cell-... --backend opencode --prompt-file worker-context.md
aegis cell session sync --cell cell-... --all
aegis cell show --cell cell-... --format html --output cell.html
```

Cell records are stored outside target repositories by default under `AEGIS_CELL_DIR`, or the platform app-data `aegis/cells` directory when that variable is unset.

## Backend support

| Backend | Status |
|---|---|
| `opencode` | Full command coverage and the default OpenCode-oriented backend. |
| `codex` | Local command adapter with session-local state files and JSON message extraction. |
| `pi` | Local command adapter using JSON event output and message reconstruction. |
| `copilot` | Conservative blocking GitHub Copilot CLI adapter; native async/resume is not claimed yet. |

Run `aegis backend detect` before choosing a backend. Detection checks local command availability only; authentication, model access, and server health still require a live smoke test.

## Documentation

- [CLI guide](./docs/cli-guide.md): install details, command examples, cells, server setup, compatibility aliases, and migration notes.
- [Contributing](./CONTRIBUTING.md): project structure, coding conventions, testing expectations, and backend-adapter guidance.
- [Live backend smoke](./docs/live-backend-smoke.md): what counts as verified backend support.
- [Package consumption](./docs/package-consumption.md): using `Aegis.Core` and `Aegis.Backends` from another .NET host.
- [Multi-backend rollout](./docs/multi-backend-rollout.md): current adapter parity and known backend differences.

## Repository layout

| Path | Purpose |
|---|---|
| `src/Aegis/` | CLI application source. |
| `src/Aegis.Core/` | Shared contracts, session registry infrastructure, state normalization, and prompt rendering. |
| `src/Aegis.Backends/` | OpenCode, Codex, Pi, and GitHub Copilot CLI backend adapters. |
| `src/Aegis/CellUi/` | Optional read-only Cell observer UI. |
| `tests/` | Unit and integration tests. |
| `prompts/` | Built-in agent prompt templates. |
| `support/vscode/` | Aegis-owned VS Code Copilot Chat agent, instruction, and prompt templates. |
| `scripts/` | Local install helpers for the CLI and VS Code support templates. |

## Build from source

```powershell
dotnet build aegis.sln
dotnet test aegis.sln
dotnet publish src/Aegis/Aegis.csproj -c Release -o "$HOME\.local\bin" --self-contained false
```

`dotnet publish` builds the optional React Cell observer UI by default. Pass `-p:BuildCellUiOnPublish=false` to skip the UI bundle; `aegis cell serve` will then show a local fallback page with build instructions.

## License

Aegis is released under the [MIT License](./LICENSE).
