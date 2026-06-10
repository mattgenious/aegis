---
name: Aegis CLI
applyTo: "**"
---

When coordinating delegated agent work, prefer the installed `aegis` command over ad hoc terminal loops or direct backend APIs. Aegis is for agents managing other agent sessions, not for human-operated CLI workflows.

Use `aegis cell` for recursive or multi-agent work. Record streams, backend sessions, clone paths, evidence, blockers, verification, and final handoffs in the cell. Use bounded supervision commands such as `aegis tail`, `aegis last-summary`, `aegis wait`, and `aegis cell session sync`.

Require delegated workers to produce a fresh `FINAL HANDOFF`; use `aegis last-summary` or cell handoff records before reading full transcripts. Treat recoverable sync states as coordinator-agent action, not as evidence of a true blocker; keep real blocker records separate.

Run `aegis backend detect` before assuming a backend is available. Detection proves local command availability only; authentication, model access, and OpenCode server health still need a live smoke when backend behavior matters. Use explicit `--backend` when a specific adapter is required.

Cell and backend state are stored outside target repositories by default. Do not commit generated cell/session state, observer UI output, or backend state files unless a task explicitly asks for a portable fixture.

VS Code support is terminal/tool based. Aegis can be used from VS Code to spawn external backend sessions through OpenCode, Codex, GitHub Copilot CLI, or other supported Aegis backends. Do not describe this as native VS Code Copilot Chat session spawning unless VS Code exposes and Aegis implements a verified backend for that API.
