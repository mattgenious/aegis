---
name: aegis-cell
description: Start or continue delegated work through an Aegis cell.
agent: Aegis Cell
argument-hint: "task goal, repo folder, backend/model if known"
---

Use Aegis to coordinate this work.

Inputs:

- Goal: `${input:goal:Describe the concrete outcome}`
- Repository or directory: `${input:directory:Use the current workspace if appropriate}`
- Preferred backend/model: `${input:backend:opencode with github-copilot/gpt-5.5 unless the task says otherwise}`

Create or continue an Aegis cell, allocate streams, run worker sessions only when useful, and record verification evidence before reporting back.
