# Aegis VS Code Support

These templates teach VS Code Copilot Chat agents how to use the installed `aegis` command from the terminal/tool loop.

They are Aegis-owned support assets only:

- `agents/aegis-cell.agent.md`: a VS Code agent profile for Aegis cell coordination.
- `instructions/aegis.instructions.md`: workspace-wide guidance for agents to prefer `aegis` for delegated coordination.
- `prompts/aegis-cell.prompt.md`: a reusable prompt for starting or continuing cell work.

The integration is terminal/tool based. VS Code agents can use Aegis to spawn and supervise external backend sessions through the CLI-shaped command contract, but Aegis does not drive native VS Code Copilot Chat tabs as backend workers.
