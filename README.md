# Aegis

Aegis is a local coordination substrate for AI agents that need to manage other AI agent sessions.

It is not designed as a human-operated CLI product. Humans install it and keep it available on PATH so their agents can launch delegated workers, preserve coordination state, recover stalled sessions, and consolidate final handoffs without inventing ad hoc terminal loops or scraping full transcripts.

## What Aegis enables agents to do

- **Delegate work across backends.** Route worker sessions through supported local backends such as OpenCode, Codex, Pi, or GitHub Copilot CLI without hardcoding each backend protocol into every coordinator agent.
- **Require final handoffs.** Wrap delegated prompts so worker agents return a `FINAL HANDOFF` summary that another agent can fetch without loading an entire transcript.
- **Persist coordination state.** Store cells, child cells, streams, clone paths, backend sessions, evidence, blockers, verification observations, and integration notes outside the target repository.
- **Supervise and recover sessions.** Let coordinator agents distinguish real blockers from recoverable stopped sessions. A stopped session without a fresh handoff becomes `needs-restart-or-nudge`, not `blocked`.
- **Fan out recursively.** Give worker agents enough durable context to split assigned work into child cells when the task needs another coordination layer.
- **Stay backend/server ready.** Keep the coordination model separated from backend transport so future integrations, including an MCP server, can build on the same records and contracts.

## Operating model

Aegis assumes an agent is the primary caller.

1. A human or bootstrap agent installs Aegis and any desired backend CLIs.
2. A coordinator agent detects available backends and creates or resumes a cell.
3. The coordinator agent launches worker sessions with scoped briefs and records them in the cell.
4. Worker agents report evidence, blockers, verification, and final handoffs.
5. The coordinator agent syncs session state, restarts or nudges recoverable sessions, and integrates the resulting work.

The command surface exists so agents have deterministic, scriptable verbs and machine-readable records. It is not the main story of the repository.

## Agent-facing contracts

- **Session records** preserve backend session ids, status, prompt metadata, summaries, and message pointers.
- **Cell records** preserve recursive coordination state across independent agent processes and terminal restarts.
- **Final handoff extraction** gives coordinator agents a bounded summary contract instead of forcing transcript replay.
- **Backend detection** reports local command availability only; authentication, model access, and OpenCode server health still require live smoke verification.
- **Generated state stays outside target repos** by default. Agents should not commit cell/session/backend state unless they are intentionally creating a fixture.

## Documentation map

- [Agent command contract](./docs/agent-command-contract.md): command forms and output contracts for agent implementers, maintainers, and local smoke tests.
- [Contributing](./CONTRIBUTING.md): source layout, agent-facing design constraints, coding conventions, and test expectations.
- [Live backend smoke](./docs/live-backend-smoke.md): what counts as verified backend support.
- [Package consumption](./docs/package-consumption.md): using `Aegis.Core` and `Aegis.Backends` from an agent host.
- [Multi-backend rollout](./docs/multi-backend-rollout.md): adapter parity and backend behavior differences.

## Repository layout

| Path | Purpose |
|---|---|
| `src/Aegis/` | CLI command surface that agents invoke. |
| `src/Aegis.Core/` | Shared contracts, session registry infrastructure, state normalization, and prompt rendering. |
| `src/Aegis.Backends/` | OpenCode, Codex, Pi, and GitHub Copilot CLI backend adapters. |
| `src/Aegis/CellUi/` | Optional read-only observer for cell records. |
| `tests/` | Unit and integration tests for agent/session behavior. |
| `prompts/` | Built-in worker and delegation prompt templates. |
| `support/vscode/` | Aegis-owned VS Code Copilot Chat agent, instruction, and prompt templates. |
| `scripts/` | Local install helpers for the CLI and VS Code support templates. |

## Build and install

Install and build details are intentionally kept in the agent command contract and contributing docs so this README can stay focused on the agent capability model. The important repo-level contract is that installing Aegis makes an `aegis` command available to agents, and `dotnet publish` can include the optional Cell observer UI from source.

## License

Aegis is released under the [MIT License](./LICENSE).
