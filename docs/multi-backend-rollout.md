# Multi-Backend Rollout Notes

## Current status

- **opencode**: production command coverage.
- **codex**: adapter implemented and primary CLI command-routing complete (`new`, `latest`, `messages`, `wait`, `abort`, `ask`, `status`, `last-summary`).
- **pi**: adapter implemented (`pi --mode json` stream parsing + session-local persistence) with primary CLI command-routing complete (`new`, `latest`, `messages`, `wait`, `abort`, `ask`, `status`, `last-summary`).

## Rollout checklist

- [x] Add backend contracts (`ISessionBackend`, backend kinds, shared message/state contracts).
- [x] Add shared registry and session metadata support.
- [x] Implement adapter abstractions for all target backends.
- [x] Wire all CLI session-oriented commands to route through the abstraction for codex/pi.
- [ ] Add end-to-end CLI command parity tests for backend-routing.
- [ ] Expand README command examples with live backend examples once command routing is validated in both adapters.
- [x] Wire ask/status/last-summary through backends for codex/pi.
- [x] Wire new/latest/messages/wait/abort through backends for codex/pi.

## Backend behavior differences

- **opencode** uses HTTP API polling and preserves existing session semantics.
- **codex** uses direct local process execution; cancellation is explicit no-op with guidance.
- **pi** uses JSON event stream mode and emits parser-friendly status/message guidance.

## Notes for next backends

- Add unit tests for message parsing of transport events before integration.
- Define failure contracts (`exit_code`, `error`, `summary`) so CLI consumers can distinguish transport issues from prompt quality.
- Add explicit guidance for unsupported or partially parsed streams to avoid corrupting summary state.
