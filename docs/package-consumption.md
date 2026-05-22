# Package Consumption

`harness-cli` is being split into libraries Baton can consume without shelling out to the CLI executable.

## Packages

- `HarnessCli.Core`: shared contracts, session registry infrastructure, state normalization, and prompt rendering.
- `HarnessCli.Backends`: OpenCode, Codex, and Pi backend adapters plus backend command orchestration.

The CLI executable remains `opencode-harness-cli` for compatibility and references the same libraries.

## Local Pack Smoke

Create packages:

```bash
dotnet pack src/HarnessCli.Core/HarnessCli.Core.csproj -c Release -o artifacts/packages
dotnet pack src/HarnessCli.Backends/HarnessCli.Backends.csproj -c Release -o artifacts/packages
```

Reference from a consumer project:

```bash
dotnet add package HarnessCli.Backends \
  --version 0.1.0 \
  --source /absolute/path/to/harness-cli/artifacts/packages
```

`HarnessCli.Backends` depends on `HarnessCli.Core`, so consumers normally reference only the backend package unless they need contracts only.

Prompt markdown files are packed as content files and copied to the consumer output so `PromptTemplates` can resolve them at runtime. They are included in both packages because NuGet content files from transitive dependencies are not copied for every consumer shape.

## Baton-Facing API Shape

Consumers should depend on `IAgentHarness` rather than OpenCode HTTP, Codex CLI, or Pi CLI details:

```csharp
AgentRunResult result = await harness.AskAsync(new AgentRunRequest
{
    Prompt = "Ship issue #123 and report the handoff.",
    Title = "Ship: issue #123",
    Model = "gpt-5.5",
    Timeout = TimeSpan.FromMinutes(10)
});

if (!result.IsSuccess)
{
    throw new InvalidOperationException(result.Error ?? result.Message);
}

Console.WriteLine(result.Summary?.Text);
```

Backend construction is still explicit at this stage. Backend/model profile configuration is planned in the next slice.
