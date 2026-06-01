# Multi-Backend Rollout Notes

## Current status

- **opencode**: production command coverage.
- **codex**: adapter implemented and primary CLI command-routing complete (`new`, `latest`, `messages`, `wait`, `abort`, `ask`, `status`, `last-summary`).
- **pi**: adapter implemented (`pi --mode json` stream parsing + session-local persistence) with primary CLI command-routing complete (`new`, `latest`, `messages`, `wait`, `abort`, `ask`, `status`, `last-summary`).
- **copilot**: adapter implemented for conservative blocking non-interactive GitHub Copilot CLI prompts with primary CLI command-routing complete (`new`, `latest`, `messages`, `wait`, `ask`, `status`, `last-summary`); native async/resume/live supervision is not claimed.

## Rollout checklist

- [x] Add backend contracts (`ISessionBackend`, backend kinds, shared message/state contracts).
- [x] Add shared registry and session metadata support.
- [x] Implement adapter abstractions for all target backends.
- [x] Wire all CLI session-oriented commands to route through the abstraction for codex/pi/copilot.
- [x] Live-smoke `ask` against opencode, codex, and pi.
- [x] Live-smoke Copilot backend through `work-map session run --backend copilot` after GitHub Copilot CLI install/auth.
- [ ] Add automated end-to-end CLI command parity tests for backend-routing where credentials/backends are available.
- [ ] Expand README command examples with live backend examples once command routing is validated in both adapters.
- [x] Wire ask/status/last-summary through backends for codex/pi/copilot.
- [x] Wire new/latest/messages/wait/abort through backends for codex/pi/copilot, with copilot abort returning unsupported guidance.

## Backend behavior differences

- **opencode** uses HTTP API polling and preserves existing session semantics.
- **codex** uses direct local process execution; cancellation is explicit no-op with guidance.
- **pi** uses JSON event stream mode and emits parser-friendly status/message guidance.
- **copilot** uses one blocking non-interactive `copilot --prompt` process per prompt, captures JSON/JSONL/plain text output into harness state, and rejects `--async` until a detached/resumable flow is implemented and live validated.

## Notes for next backends

- Add unit tests for message parsing of transport events before integration.
- Define failure contracts (`exit_code`, `error`, `summary`) so CLI consumers can distinguish transport issues from prompt quality.
- Add explicit guidance for unsupported or partially parsed streams to avoid corrupting summary state.
