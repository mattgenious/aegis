---
name: Aegis CLI
applyTo: "**"
---

When coordinating delegated agent work, prefer the installed `aegis` command over ad hoc terminal loops or direct backend APIs.

Use `aegis cell` for recursive or multi-agent work. Record streams, backend sessions, clone paths, evidence, blockers, verification, and final handoffs in the cell. Use bounded supervision commands such as `aegis tail`, `aegis last-summary`, `aegis wait`, and `aegis cell session sync`.

VS Code support is terminal/tool based. Aegis can be used from VS Code to spawn external backend sessions through OpenCode, Codex, GitHub Copilot CLI, or other supported Aegis backends. Do not describe this as native VS Code Copilot Chat session spawning unless VS Code exposes and Aegis implements a verified backend for that API.
