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
  --version 0.1.1 \
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

Backend construction remains explicit so hosts can decide how to instantiate and lifetime-manage each backend.

## Backend And Model Profiles

Use `AgentProfileResolver` to keep Baton worker intent separate from backend/model transport details:

```csharp
var profiles = new AgentHarnessConfiguration
{
    DefaultBackend = BackendKind.Opencode,
    Profiles = new Dictionary<string, AgentModelProfile>(StringComparer.OrdinalIgnoreCase)
    {
        ["fast"] = new AgentModelProfile
        {
            Backend = BackendKind.Opencode,
            ModelProvider = "github-copilot",
            Model = "gpt-5.4-mini",
            Variant = "low",
            Timeout = TimeSpan.FromMinutes(5)
        },
        ["cheap"] = new AgentModelProfile
        {
            Backend = BackendKind.Opencode,
            ModelProvider = "opencode",
            Model = "deepseek-v4-flash-free",
            Timeout = TimeSpan.FromMinutes(5)
        },
        ["deep"] = new AgentModelProfile
        {
            Backend = BackendKind.Opencode,
            ModelProvider = "github-copilot",
            Model = "gpt-5.5",
            Variant = "high",
            Timeout = TimeSpan.FromMinutes(20)
        }
    }
};

ResolvedAgentProfile resolved = new AgentProfileResolver(profiles).Resolve(new AgentProfileSelection
{
    Profile = "deep",
    Variant = "xhigh"
});
```

Resolution order is explicit override, named profile, then configuration default. The CLI exposes the same idea with `--profile fast`, `--profile cheap`, and `--profile deep`; explicit flags still override profile values.
