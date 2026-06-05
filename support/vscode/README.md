# Aegis VS Code Support

These templates teach VS Code Copilot Chat how to use the installed `aegis` CLI from the terminal/tool loop.

They are Aegis-owned support assets only:

- `agents/aegis-cell.agent.md`: a VS Code agent profile for Aegis cell coordination.
- `instructions/aegis.instructions.md`: workspace-wide guidance to prefer `aegis` for delegated coordination.
- `prompts/aegis-cell.prompt.md`: a reusable prompt for starting or continuing cell work.

The integration is terminal/tool based. Aegis can spawn and supervise external backend sessions from VS Code through the CLI, but it does not drive native VS Code Copilot Chat tabs as backend workers.
