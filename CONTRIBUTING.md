# Aegis Development Conventions

## Codebase structure

- `src/Aegis.Core/` contains reusable contracts, session registry infrastructure, state normalization, and prompt rendering.
- `src/Aegis.Backends/` contains reusable backend adapters and transport helpers.
- `src/Aegis/` is the CLI-shaped command surface that agents invoke.
- `tests/Aegis.UnitTests/` is for pure unit tests.
- `tests/Aegis.IntegrationTests/` is for CLI/process-level tests.
- `docs/` contains product and architecture docs.
- `prompts/` contains built-in agent prompt templates as markdown, grouped by theme/backend.

The app binary is `aegis`. Keep `opencode-aegis` only as an explicit migration alias until compatibility support is intentionally removed.

## Source organization

- Keep root bootstrap in `src/Aegis/Program.cs` minimal.
- Command handlers should live in `src/Aegis/Commands/`.
- Backend abstraction implementations in `src/Aegis.Backends/Backends/`.
- Shared domain/session types in `src/Aegis.Core/Core/`.
- Shared infrastructure helpers in `src/Aegis.Core/Infrastructure/`.
- Built-in prompt bodies must live under `prompts/**/*.md`; C# may render templates but must not hide system/delegation prompt text in string literals.

## File and member limits

- Target 350 lines max per file, 500 hard cap.
- Prefer ≤220 lines per command handler, hard cap 320.
- Prefer ≤60 lines per method, hard cap 120.
- If a file or method exceeds these limits, split it before merging.

## API / typing policy

- Nullable context is required.
- `public` API should be explicit and documented with XML docs where stable.
- No `dynamic`; no `object`-typed payload handling.
- Use `record` types for immutable DTO/message contracts.

## Error handling and logging

- Catch and normalize command-level errors to stable agent-readable messages.
- Avoid swallowing exceptions.
- Process failures should include context (backend, command, session id when available).

## Testing policy

- Tests are required for every issue that changes orchestration or parser behavior.
- Add at least one positive and one negative test per new branch of behavior.
- Parser and session-contract regressions are blocking for merge.

## Sorting and style

- Sort namespaces: `System...`, third-party, then project namespaces.
- Sort type members by public → internal → private.
- Keep command names and option enums in stable, predictable order.

## Project hygiene

- Use `dotnet format` in CI or before major merges.
- Prefer no custom formatting scripts beyond standard `.editorconfig` and analyzers.
- PRs should not include refactors unrelated to the issue at hand.

## Adding a backend adapter

- Add the backend contract via `ISessionBackend` in `src/Aegis/Backends/`.
- Keep mapping responsibilities in one place (`SessionStateNormalizer`, `SessionRegistryService`, etc.) and keep command adapters thin.
- Persist status and message state in deterministic JSON files per session so resume/troubleshooting tools can operate independently of the transport.
- Add adapter-level unit tests for:
  - session creation
  - status transitions
  - message parsing/summarization from the target transport format
  - explicit failure guidance when transport output is unsupported
- Document command coverage and streaming/abort limitations in README and mark any non-wired feature flags clearly.
