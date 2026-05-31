using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessCli.Backends;
using HarnessCli.Core;
using HarnessCli.Infrastructure;

namespace OpencodeHarnessCli;

internal static partial class Program
{
    private static async Task<int> RunWorkMapCommand(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintWorkMapHelp();
            return 0;
        }

        var options = WorkMapArgs.Parse(args);
        var store = new FileWorkMapStore();

        return options.Positionals switch
        {
            ["create", ..] => await WorkMapCreate(store, options),
            ["list", ..] => await WorkMapList(store, options),
            ["show", ..] => await WorkMapShow(store, options),
            ["brief", ..] => await WorkMapBrief(store, options),
            ["stream", "add", ..] => await WorkMapStreamAdd(store, options),
            ["session", "link", ..] => await WorkMapSessionLink(store, options),
            ["session", "run", ..] => await WorkMapSessionRun(store, options),
            ["session", "sync", ..] => await WorkMapSessionSync(store, options),
            ["evidence", "add", ..] => await WorkMapEvidenceAdd(store, options),
            _ => Fail($"Unknown work-map command '{string.Join(' ', options.Positionals)}'. Run `opencode-harness-cli help work-map`.")
        };
    }

    private static async Task<int> WorkMapCreate(IWorkMapStore store, WorkMapArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = new WorkMapMissionRecord
        {
            Id = options.MissionId ?? NewWorkMapId("mission"),
            Title = Require(options.Title, "--title"),
            Intent = options.Intent,
            Status = options.Status ?? "planned",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            NextAction = options.NextAction,
            Events =
            [
                new WorkMapEventRecord
                {
                    AtUtc = now,
                    Type = "created",
                    Summary = "Mission created."
                }
            ]
        };

        await store.SaveMissionAsync(mission);
        WriteJson(JsonSerializer.SerializeToNode(mission, JsonOptions));
        return 0;
    }

    private static async Task<int> WorkMapList(IWorkMapStore store, WorkMapArgs options)
    {
        var missions = await store.GetMissionsAsync();
        var ordered = missions.OrderByDescending(item => item.UpdatedAtUtc).ToArray();
        if (IsMarkdown(options.Format))
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Work Maps");
            builder.AppendLine();
            foreach (var mission in ordered)
            {
                builder.AppendLine($"- `{mission.Id}` - {mission.Title} ({mission.Status})");
            }

            await WriteWorkMapOutput(builder.ToString(), options);
            return 0;
        }

        WriteJson(JsonSerializer.SerializeToNode(ordered, JsonOptions));
        return 0;
    }

    private static async Task<int> WorkMapStreamAdd(IWorkMapStore store, WorkMapArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var streamId = options.StreamId ?? NewWorkMapId("stream");
        var workstream = new WorkMapWorkstreamRecord
        {
            Id = streamId,
            MissionId = mission.Id,
            Name = Require(options.Name, "--name"),
            Role = options.Role,
            Target = options.Target,
            ClonePath = NormalizeOptionalPath(options.ClonePath),
            SourceRepoPath = NormalizeOptionalPath(options.SourceRepoPath),
            Branch = options.Branch,
            Status = options.Status ?? "planned",
            DependsOn = options.DependsOn.ToList(),
            IntegrationAction = options.IntegrationAction,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var missionStreams = mission.WorkstreamIds.ToList();
        AddUnique(missionStreams, streamId);
        var edges = mission.Edges.ToList();
        foreach (var dependency in options.DependsOn)
        {
            edges.Add(new WorkMapEdgeRecord
            {
                FromId = streamId,
                ToId = dependency,
                Kind = "dependsOn"
            });
        }

        var events = mission.Events.ToList();
        events.Add(new WorkMapEventRecord
        {
            AtUtc = now,
            Type = "workstreamAdded",
            Summary = $"Added workstream '{workstream.Name}'."
        });

        mission = mission with
        {
            WorkstreamIds = missionStreams,
            Edges = edges,
            Events = events,
            Status = mission.Status == "planned" ? "in-progress" : mission.Status,
            UpdatedAtUtc = now
        };

        await store.SaveWorkstreamAsync(workstream);
        await store.SaveMissionAsync(mission);
        WriteJson(JsonSerializer.SerializeToNode(workstream, JsonOptions));
        return 0;
    }

    private static async Task<int> WorkMapSessionLink(IWorkMapStore store, WorkMapArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var workstream = await RequireWorkstream(store, options.StreamId);
        EnsureMissionOwnsWorkstream(mission, workstream);

        var sessionId = Require(options.SessionId, "--session");
        var backend = options.Backend ?? InferBackendFromSessionId(sessionId) ?? "codex";
        var session = new WorkMapAgentSessionRecord
        {
            Id = sessionId,
            MissionId = mission.Id,
            WorkstreamId = workstream.Id,
            DisplayName = options.DisplayName ?? GenerateDisplayName(sessionId),
            Title = options.Title,
            Role = options.Role,
            Backend = backend,
            BackendSessionId = options.BackendSessionId,
            Provider = options.Provider,
            Model = options.Model,
            Variant = options.Variant,
            Directory = NormalizeOptionalPath(options.Directory) ?? workstream.ClonePath,
            Status = options.Status ?? "linked",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Events =
            [
                new WorkMapEventRecord
                {
                    AtUtc = now,
                    Type = "linked",
                    Summary = "Existing session linked to work map."
                }
            ]
        };

        await SaveSessionAttachment(store, mission, workstream, session, now, "Session linked.");
        WriteJson(JsonSerializer.SerializeToNode(session, JsonOptions));
        return 0;
    }

    private static async Task<int> WorkMapSessionRun(IWorkMapStore store, WorkMapArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var workstream = await RequireWorkstream(store, options.StreamId);
        EnsureMissionOwnsWorkstream(mission, workstream);

        var prompt = await ReadWorkMapPrompt(options);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Fail("Prompt is required. Use --prompt or --prompt-file.");
        }

        var resolved = ResolveWorkMapProfile(options);
        using var http = CreateHttpClient(options.Server ?? DefaultServer);
        var client = new OpenCodeClient(http);
        var backend = CreateBackend(resolved.Backend, client);
        var commands = new BackendCommandService(backend, new SessionRegistryService(new FileSessionRegistry()));
        var directory = NormalizeOptionalPath(options.Directory) ?? workstream.ClonePath;
        var request = new PromptRequest(
            Text: prompt,
            SourceKind: !string.IsNullOrWhiteSpace(options.PromptFile) ? PromptSourceKind.File : PromptSourceKind.Inline,
            SourceLocation: options.PromptFile,
            ModelProvider: resolved.ModelProvider,
            Model: resolved.Model,
            Variant: resolved.Variant,
            SummaryMarker: options.SummaryMarker,
            Directory: directory,
            Agent: resolved.Agent,
            System: resolved.System,
            NoReply: options.NoReply,
            Raw: options.Raw);

        var result = await commands.AskAsync(new BackendAskRequest(
            SessionId: null,
            Title: options.Title ?? workstream.Name,
            ParentSessionId: null,
            Directory: directory,
            Prompt: request,
            Async: options.Async,
            Wait: options.Wait,
            Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds)));

        var status = result.Summary is not null
            ? "handoff"
            : options.Async && !options.Wait
                ? "queued"
                : "waiting";
        WorkMapBlockerRecord? blocker = null;
        if (!result.PostResult.IsSuccess)
        {
            status = "blocked";
            blocker = new WorkMapBlockerRecord
            {
                AtUtc = now,
                Summary = result.PostResult.Message ?? "Backend prompt failed.",
                Evidence = result.PostResult.Error
            };
        }

        var events = new List<WorkMapEventRecord>
        {
            new()
            {
                AtUtc = now,
                Type = "promptSent",
                Summary = $"Prompt sent through {backend.Kind.ToOptionValue()}."
            }
        };
        if (result.Summary is not null)
        {
            events.Add(new WorkMapEventRecord
            {
                AtUtc = DateTimeOffset.UtcNow,
                Type = "finalHandoffFound",
                Summary = "Worker returned a final handoff."
            });
        }

        var session = new WorkMapAgentSessionRecord
        {
            Id = result.Session.SessionId,
            MissionId = mission.Id,
            WorkstreamId = workstream.Id,
            DisplayName = options.DisplayName ?? GenerateDisplayName(result.Session.SessionId),
            Title = options.Title ?? workstream.Name,
            Role = options.Role ?? workstream.Role,
            Backend = backend.Kind.ToOptionValue(),
            BackendSessionId = result.Session.BackendSessionId,
            Provider = resolved.ModelProvider,
            Model = resolved.Model,
            Variant = resolved.Variant,
            Directory = directory,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Events = events,
            FinalHandoff = result.Summary is null
                ? null
                : new WorkMapHandoffRecord
                {
                    AtUtc = DateTimeOffset.UtcNow,
                    Text = result.Summary.Text
                },
            Blocker = blocker
        };

        await SaveSessionAttachment(store, mission, workstream, session, DateTimeOffset.UtcNow, "Session run recorded.");

        if (blocker is not null)
        {
            return Fail(blocker.Evidence is null ? blocker.Summary : $"{blocker.Summary}: {blocker.Evidence}");
        }

        WriteJson(new JsonObject
        {
            ["missionID"] = mission.Id,
            ["workstreamID"] = workstream.Id,
            ["sessionID"] = session.Id,
            ["displayName"] = session.DisplayName,
            ["backend"] = session.Backend,
            ["status"] = session.Status,
            ["summary"] = session.FinalHandoff?.Text
        });
        return 0;
    }

    private static async Task<int> WorkMapSessionSync(IWorkMapStore store, WorkMapArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await RequireAgentSession(store, options.SessionId);
        var backendKind = ParseBackend(session.Backend);
        using var http = CreateHttpClient(options.Server ?? DefaultServer);
        var backend = CreateBackend(backendKind, new OpenCodeClient(http));
        var commands = new BackendCommandService(backend, new SessionRegistryService(new FileSessionRegistry()));
        var states = await commands.GetStatusAsync(session.Id);
        var state = states.FirstOrDefault();
        var summary = await commands.GetLastSummaryAsync(session.Id, options.SummaryMarker);
        var events = session.Events.ToList();
        events.Add(new WorkMapEventRecord
        {
            AtUtc = now,
            Type = "synced",
            Summary = state is null ? "No status snapshot returned." : $"Status observed as {state.EffectiveStatus}."
        });

        session = session with
        {
            Status = summary is not null ? "handoff" : state?.EffectiveStatus ?? session.Status,
            UpdatedAtUtc = now,
            Events = events,
            FinalHandoff = summary is null ? session.FinalHandoff : new WorkMapHandoffRecord
            {
                AtUtc = now,
                Text = summary.Text
            }
        };

        await store.SaveAgentSessionAsync(session);
        WriteJson(JsonSerializer.SerializeToNode(session, JsonOptions));
        return 0;
    }

    private static async Task<int> WorkMapEvidenceAdd(IWorkMapStore store, WorkMapArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var evidence = new WorkMapEvidenceRecord
        {
            Id = NewWorkMapId("evidence"),
            Kind = options.Kind ?? "note",
            Path = NormalizeOptionalPath(options.Path),
            Summary = options.Summary,
            AddedAtUtc = now
        };

        if (!string.IsNullOrWhiteSpace(options.StreamId))
        {
            var workstream = await RequireWorkstream(store, options.StreamId);
            EnsureMissionOwnsWorkstream(mission, workstream);
            var evidenceList = workstream.Evidence.ToList();
            evidenceList.Add(evidence);
            await store.SaveWorkstreamAsync(workstream with
            {
                Evidence = evidenceList,
                UpdatedAtUtc = now
            });
        }

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            var session = await RequireAgentSession(store, options.SessionId);
            var evidenceList = session.Evidence.ToList();
            evidenceList.Add(evidence);
            await store.SaveAgentSessionAsync(session with
            {
                Evidence = evidenceList,
                UpdatedAtUtc = now
            });
        }

        if (string.IsNullOrWhiteSpace(options.StreamId) && string.IsNullOrWhiteSpace(options.SessionId))
        {
            var evidenceList = mission.Evidence.ToList();
            evidenceList.Add(evidence);
            await store.SaveMissionAsync(mission with
            {
                Evidence = evidenceList,
                UpdatedAtUtc = now
            });
        }

        WriteJson(JsonSerializer.SerializeToNode(evidence, JsonOptions));
        return 0;
    }

    private static async Task<int> WorkMapShow(IWorkMapStore store, WorkMapArgs options)
    {
        var mission = await RequireMission(store, options.MissionId);
        var workstreams = await store.GetWorkstreamsAsync(mission.Id);
        var sessions = await store.GetAgentSessionsAsync(mission.Id);
        var bundle = new WorkMapBundle(mission, workstreams, sessions);

        if (IsMarkdown(options.Format))
        {
            await WriteWorkMapOutput(BuildWorkMapMarkdown(bundle), options);
            return 0;
        }

        if (options.Format.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            await WriteWorkMapOutput(BuildWorkMapHtml(bundle), options);
            return 0;
        }

        WriteJson(JsonSerializer.SerializeToNode(bundle, JsonOptions));
        return 0;
    }

    private static async Task<int> WorkMapBrief(IWorkMapStore store, WorkMapArgs options)
    {
        var mission = await RequireMission(store, options.MissionId);
        var workstream = await RequireWorkstream(store, options.StreamId);
        EnsureMissionOwnsWorkstream(mission, workstream);
        var sessions = await store.GetAgentSessionsAsync(mission.Id);
        var relevantSessions = sessions.Where(item => item.WorkstreamId == workstream.Id).ToArray();
        var brief = BuildDelegationBrief(mission, workstream, relevantSessions);
        await WriteWorkMapOutput(brief, options);
        return 0;
    }

    private static async Task SaveSessionAttachment(
        IWorkMapStore store,
        WorkMapMissionRecord mission,
        WorkMapWorkstreamRecord workstream,
        WorkMapAgentSessionRecord session,
        DateTimeOffset now,
        string eventSummary)
    {
        var missionSessions = mission.SessionIds.ToList();
        AddUnique(missionSessions, session.Id);
        var workstreamSessions = workstream.SessionIds.ToList();
        AddUnique(workstreamSessions, session.Id);
        var events = mission.Events.ToList();
        events.Add(new WorkMapEventRecord
        {
            AtUtc = now,
            Type = "sessionAttached",
            Summary = eventSummary
        });

        await store.SaveAgentSessionAsync(session);
        await store.SaveWorkstreamAsync(workstream with
        {
            SessionIds = workstreamSessions,
            Status = session.Status is "handoff" or "blocked" ? session.Status : workstream.Status,
            UpdatedAtUtc = now
        });
        await store.SaveMissionAsync(mission with
        {
            SessionIds = missionSessions,
            Events = events,
            Status = mission.Status == "planned" ? "in-progress" : mission.Status,
            UpdatedAtUtc = now
        });
    }

    private static async Task<WorkMapMissionRecord> RequireMission(IWorkMapStore store, string? missionId)
    {
        var id = Require(missionId, "--mission");
        var mission = await store.GetMissionAsync(id);
        return mission ?? throw new ArgumentException($"Unknown work-map mission '{id}'.");
    }

    private static async Task<WorkMapWorkstreamRecord> RequireWorkstream(IWorkMapStore store, string? streamId)
    {
        var id = Require(streamId, "--stream");
        var workstream = await store.GetWorkstreamAsync(id);
        return workstream ?? throw new ArgumentException($"Unknown work-map workstream '{id}'.");
    }

    private static async Task<WorkMapAgentSessionRecord> RequireAgentSession(IWorkMapStore store, string? sessionId)
    {
        var id = Require(sessionId, "--session");
        var session = await store.GetAgentSessionAsync(id);
        return session ?? throw new ArgumentException($"Unknown work-map agent session '{id}'.");
    }

    private static void EnsureMissionOwnsWorkstream(WorkMapMissionRecord mission, WorkMapWorkstreamRecord workstream)
    {
        if (!string.Equals(workstream.MissionId, mission.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Workstream '{workstream.Id}' belongs to mission '{workstream.MissionId}', not '{mission.Id}'.");
        }
    }

    private static ResolvedAgentProfile ResolveWorkMapProfile(WorkMapArgs options)
    {
        BackendKind? backend = null;
        if (!string.IsNullOrWhiteSpace(options.Backend))
        {
            backend = ParseBackend(options.Backend);
        }

        return new AgentProfileResolver(DefaultAgentConfiguration with { DefaultBackend = BackendKind.Codex }).Resolve(
            new AgentProfileSelection
            {
                Profile = options.Profile,
                Backend = backend,
                Model = options.Model,
                Variant = options.Variant,
                Agent = options.Agent,
                System = options.System,
                Timeout = options.TimeoutWasProvided ? TimeSpan.FromSeconds(options.TimeoutSeconds) : null
            });
    }

    private static BackendKind ParseBackend(string value)
    {
        if (!BackendKindExtensions.TryParse(value, out var backend))
        {
            throw new ArgumentException($"Unsupported backend '{value}'. Use opencode, codex, or pi.");
        }

        return backend;
    }

    private static async Task<string> ReadWorkMapPrompt(WorkMapArgs options)
    {
        if (!string.IsNullOrWhiteSpace(options.Prompt)) return options.Prompt;
        if (!string.IsNullOrWhiteSpace(options.PromptFile))
        {
            if (!File.Exists(options.PromptFile)) throw new ArgumentException($"--prompt-file not found: {options.PromptFile}");
            return await File.ReadAllTextAsync(options.PromptFile);
        }

        if (Console.IsInputRedirected) return await Console.In.ReadToEndAsync();
        return string.Empty;
    }

    private static string BuildWorkMapMarkdown(WorkMapBundle bundle)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {bundle.Mission.Title}");
        builder.AppendLine();
        builder.AppendLine($"- Mission: `{bundle.Mission.Id}`");
        builder.AppendLine($"- Status: {bundle.Mission.Status}");
        if (!string.IsNullOrWhiteSpace(bundle.Mission.Intent)) builder.AppendLine($"- Intent: {bundle.Mission.Intent}");
        if (!string.IsNullOrWhiteSpace(bundle.Mission.NextAction)) builder.AppendLine($"- Next action: {bundle.Mission.NextAction}");
        builder.AppendLine();
        builder.AppendLine("## Workstreams");
        builder.AppendLine();
        builder.AppendLine("| Workstream | Role | Clone | Sessions | Status | Evidence | Integration |");
        builder.AppendLine("|------------|------|-------|----------|--------|----------|-------------|");
        foreach (var stream in bundle.Workstreams)
        {
            builder.AppendLine(
                $"| `{stream.Id}` {EscapePipe(stream.Name)} | {EscapePipe(stream.Role)} | {EscapePipe(stream.ClonePath)} | {stream.SessionIds.Count} | {EscapePipe(stream.Status)} | {stream.Evidence.Count} | {EscapePipe(stream.IntegrationAction)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Sessions");
        builder.AppendLine();
        builder.AppendLine("| Session | Display | Backend | Model | Status | Handoff |");
        builder.AppendLine("|---------|---------|---------|-------|--------|---------|");
        foreach (var session in bundle.Sessions)
        {
            var handoff = session.FinalHandoff?.Text is null ? "" : FirstLine(session.FinalHandoff.Text);
            builder.AppendLine(
                $"| `{session.Id}` | {EscapePipe(session.DisplayName)} | {EscapePipe(session.Backend)} | {EscapePipe(session.Model)} | {EscapePipe(session.Status)} | {EscapePipe(handoff)} |");
        }

        return builder.ToString();
    }

    private static string BuildWorkMapHtml(WorkMapBundle bundle)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine($"<title>{Html(bundle.Mission.Title)} - Work Map</title>");
        builder.AppendLine("<style>body{font-family:system-ui,sans-serif;margin:32px;line-height:1.4;color:#17202a;background:#f7f8fb}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:16px}.card{background:white;border:1px solid #d8dee9;border-radius:8px;padding:16px}.meta{color:#596579;font-size:13px}.status{font-weight:700}</style></head><body>");
        builder.AppendLine($"<h1>{Html(bundle.Mission.Title)}</h1>");
        builder.AppendLine($"<p class=\"meta\">Mission {Html(bundle.Mission.Id)} · {Html(bundle.Mission.Status)}</p>");
        if (!string.IsNullOrWhiteSpace(bundle.Mission.Intent)) builder.AppendLine($"<p>{Html(bundle.Mission.Intent)}</p>");
        builder.AppendLine("<h2>Agent Sessions</h2><div class=\"grid\">");
        foreach (var session in bundle.Sessions)
        {
            builder.AppendLine("<section class=\"card\">");
            builder.AppendLine($"<h3>{Html(session.DisplayName ?? session.Id)}</h3>");
            builder.AppendLine($"<p class=\"meta\">{Html(session.Backend)} {Html(session.Provider)} {Html(session.Model)} · {Html(session.WorkstreamId)}</p>");
            builder.AppendLine($"<p class=\"status\">{Html(session.Status)}</p>");
            if (session.FinalHandoff is not null) builder.AppendLine($"<p>{Html(FirstLine(session.FinalHandoff.Text))}</p>");
            if (session.Blocker is not null) builder.AppendLine($"<p><strong>Blocker:</strong> {Html(session.Blocker.Summary)}</p>");
            builder.AppendLine($"<p class=\"meta\">Evidence: {session.Evidence.Count} · Events: {session.Events.Count}</p>");
            builder.AppendLine("</section>");
        }

        builder.AppendLine("</div><h2>Workstreams</h2><div class=\"grid\">");
        foreach (var stream in bundle.Workstreams)
        {
            builder.AppendLine("<section class=\"card\">");
            builder.AppendLine($"<h3>{Html(stream.Name)}</h3>");
            builder.AppendLine($"<p class=\"meta\">{Html(stream.Id)} · {Html(stream.Role)} · {Html(stream.Status)}</p>");
            if (!string.IsNullOrWhiteSpace(stream.ClonePath)) builder.AppendLine($"<p><code>{Html(stream.ClonePath)}</code></p>");
            if (!string.IsNullOrWhiteSpace(stream.Target)) builder.AppendLine($"<p>{Html(stream.Target)}</p>");
            builder.AppendLine("</section>");
        }

        builder.AppendLine("</div></body></html>");
        return builder.ToString();
    }

    private static string BuildDelegationBrief(
        WorkMapMissionRecord mission,
        WorkMapWorkstreamRecord workstream,
        IReadOnlyList<WorkMapAgentSessionRecord> sessions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Delegation Brief");
        builder.AppendLine();
        builder.AppendLine("## Role Or Stance");
        builder.AppendLine();
        builder.AppendLine(workstream.Role ?? "- Use the role that best completes this workstream.");
        builder.AppendLine();
        builder.AppendLine("## Objective");
        builder.AppendLine();
        builder.AppendLine(workstream.Target ?? workstream.Name);
        builder.AppendLine();
        builder.AppendLine("## Coordinator Context");
        builder.AppendLine();
        builder.AppendLine($"- Mission: `{mission.Id}` - {mission.Title}");
        if (!string.IsNullOrWhiteSpace(mission.Intent)) builder.AppendLine($"- Intent: {mission.Intent}");
        builder.AppendLine($"- Workstream: `{workstream.Id}` - {workstream.Name}");
        if (workstream.DependsOn.Count > 0) builder.AppendLine($"- Depends on: {string.Join(", ", workstream.DependsOn)}");
        builder.AppendLine();
        builder.AppendLine("## Assigned Clone Or Session");
        builder.AppendLine();
        builder.AppendLine($"- Clone path: {workstream.ClonePath ?? "(not assigned)"}");
        builder.AppendLine($"- Source repo: {workstream.SourceRepoPath ?? "(not recorded)"}");
        builder.AppendLine($"- Branch: {workstream.Branch ?? "(not recorded)"}");
        foreach (var session in sessions)
        {
            builder.AppendLine($"- Existing session: `{session.Id}` ({session.DisplayName ?? session.Backend})");
        }

        builder.AppendLine();
        builder.AppendLine("## Autonomy And Boundaries");
        builder.AppendLine();
        builder.AppendLine("- Make useful progress inside the assigned scope without waiting for permission when the repo instructions allow it.");
        builder.AppendLine("- Do not revert or overwrite unrelated work. Assume other agents may be editing other clones or slices.");
        builder.AppendLine("- Keep changes and conclusions scoped to this workstream unless the coordinator asks for expansion.");
        builder.AppendLine();
        builder.AppendLine("## Expected Output");
        builder.AppendLine();
        builder.AppendLine("- A concise final handoff with outcome, files changed or facts found, commands run, evidence, and residual risk.");
        builder.AppendLine();
        builder.AppendLine("## Evidence To Return");
        builder.AppendLine();
        builder.AppendLine("- Diffs, logs, test output, source paths, session exports, screenshots, or exact blocker evidence that the coordinator can inspect.");
        builder.AppendLine();
        builder.AppendLine("## Stop Or Report A Blocker When");
        builder.AppendLine();
        builder.AppendLine("- The assigned clone/session is unavailable, instructions conflict, a required permission or secret is missing, or continuing would risk corrupting unrelated work.");
        return builder.ToString();
    }

    private static async Task WriteWorkMapOutput(string text, WorkMapArgs options)
    {
        if (string.IsNullOrWhiteSpace(options.Output))
        {
            Console.Write(text);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? Environment.CurrentDirectory);
        await File.WriteAllTextAsync(options.Output, text, Encoding.UTF8);
    }

    private static string NewWorkMapId(string prefix) => $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..(prefix.Length + 24)];

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static bool IsMarkdown(string format) =>
        format.Equals("md", StringComparison.OrdinalIgnoreCase)
        || format.Equals("markdown", StringComparison.OrdinalIgnoreCase);

    private static string EscapePipe(string? value) =>
        (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FirstLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    private static string? InferBackendFromSessionId(string sessionId)
    {
        var separator = sessionId.IndexOf('-');
        if (separator <= 0) return null;
        var candidate = sessionId[..separator];
        return BackendKindExtensions.TryParse(candidate, out var backend) ? backend.ToOptionValue() : null;
    }

    private static string GenerateDisplayName(string seed)
    {
        string[] adjectives = ["Patient", "Bright", "Steady", "Neon", "Brave", "Calm", "Quick", "Clear"];
        string[] nouns = ["Falcon", "Badger", "Otter", "Comet", "Anchor", "Lantern", "Harbor", "Vector"];
        var hash = (uint)seed.GetHashCode(StringComparison.Ordinal);
        return $"{adjectives[(int)(hash % (uint)adjectives.Length)]} {nouns[(int)((hash / (uint)adjectives.Length) % (uint)nouns.Length)]}";
    }

    private sealed record WorkMapBundle(
        WorkMapMissionRecord Mission,
        IReadOnlyList<WorkMapWorkstreamRecord> Workstreams,
        IReadOnlyList<WorkMapAgentSessionRecord> Sessions);

    private sealed class WorkMapArgs
    {
        public List<string> Positionals { get; } = [];

        public string? MissionId { get; private set; }

        public string? StreamId { get; private set; }

        public string? SessionId { get; private set; }

        public string? Title { get; private set; }

        public string? Name { get; private set; }

        public string? Intent { get; private set; }

        public string? Role { get; private set; }

        public string? Target { get; private set; }

        public string? ClonePath { get; private set; }

        public string? SourceRepoPath { get; private set; }

        public string? Branch { get; private set; }

        public string? IntegrationAction { get; private set; }

        public string? DisplayName { get; private set; }

        public string? Backend { get; private set; }

        public string? BackendSessionId { get; private set; }

        public string? Provider { get; private set; }

        public string? Model { get; private set; }

        public string? Variant { get; private set; }

        public string? Agent { get; private set; }

        public string? System { get; private set; }

        public string? Directory { get; private set; }

        public string? Server { get; private set; }

        public string? Profile { get; private set; }

        public string? Prompt { get; private set; }

        public string? PromptFile { get; private set; }

        public string Format { get; private set; } = "json";

        public string? Output { get; private set; }

        public string? Status { get; private set; }

        public string? NextAction { get; private set; }

        public string? Kind { get; private set; }

        public string? Path { get; private set; }

        public string? Summary { get; private set; }

        public List<string> DependsOn { get; } = [];

        public bool Async { get; private set; }

        public bool Wait { get; private set; }

        public bool Raw { get; private set; }

        public bool NoReply { get; private set; }

        public int TimeoutSeconds { get; private set; } = 300;

        public bool TimeoutWasProvided { get; private set; }

        public string SummaryMarker { get; private set; } = "FINAL HANDOFF";

        public static WorkMapArgs Parse(IEnumerable<string> args)
        {
            var parsed = new WorkMapArgs();
            var queue = new Queue<string>(args);
            while (queue.Count > 0)
            {
                var arg = queue.Dequeue();
                switch (arg)
                {
                    case "--mission": parsed.MissionId = Value(queue, arg); break;
                    case "--stream": parsed.StreamId = Value(queue, arg); break;
                    case "--session": parsed.SessionId = Value(queue, arg); break;
                    case "--title": parsed.Title = Value(queue, arg); break;
                    case "--name": parsed.Name = Value(queue, arg); break;
                    case "--intent": parsed.Intent = Value(queue, arg); break;
                    case "--role": parsed.Role = Value(queue, arg); break;
                    case "--target": parsed.Target = Value(queue, arg); break;
                    case "--clone": parsed.ClonePath = Value(queue, arg); break;
                    case "--clone-path": parsed.ClonePath = Value(queue, arg); break;
                    case "--source-repo": parsed.SourceRepoPath = Value(queue, arg); break;
                    case "--branch": parsed.Branch = Value(queue, arg); break;
                    case "--depends-on": parsed.DependsOn.Add(Value(queue, arg)); break;
                    case "--integration-action": parsed.IntegrationAction = Value(queue, arg); break;
                    case "--display-name": parsed.DisplayName = Value(queue, arg); break;
                    case "--backend": parsed.Backend = Value(queue, arg); break;
                    case "--engine": parsed.Backend = Value(queue, arg); break;
                    case "--backend-session": parsed.BackendSessionId = Value(queue, arg); break;
                    case "--provider": parsed.Provider = Value(queue, arg); break;
                    case "--model": parsed.Model = Value(queue, arg); break;
                    case "--variant": parsed.Variant = Value(queue, arg); break;
                    case "--reasoning": parsed.Variant = Value(queue, arg); break;
                    case "--agent": parsed.Agent = Value(queue, arg); break;
                    case "--system": parsed.System = Value(queue, arg); break;
                    case "--directory": parsed.Directory = Value(queue, arg); break;
                    case "--server": parsed.Server = Value(queue, arg); break;
                    case "--profile": parsed.Profile = Value(queue, arg); break;
                    case "--prompt": parsed.Prompt = Value(queue, arg); break;
                    case "--prompt-file": parsed.PromptFile = Value(queue, arg); break;
                    case "--format": parsed.Format = Value(queue, arg); break;
                    case "--output": parsed.Output = Value(queue, arg); break;
                    case "--status": parsed.Status = Value(queue, arg); break;
                    case "--next-action": parsed.NextAction = Value(queue, arg); break;
                    case "--kind": parsed.Kind = Value(queue, arg); break;
                    case "--path": parsed.Path = Value(queue, arg); break;
                    case "--summary": parsed.Summary = Value(queue, arg); break;
                    case "--summary-marker": parsed.SummaryMarker = Value(queue, arg); break;
                    case "--async": parsed.Async = true; break;
                    case "--wait": parsed.Wait = true; break;
                    case "--raw": parsed.Raw = true; break;
                    case "--no-reply": parsed.NoReply = true; break;
                    case "--timeout":
                        parsed.TimeoutSeconds = PositiveInt(Value(queue, arg), arg);
                        parsed.TimeoutWasProvided = true;
                        break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"Unknown work-map option '{arg}'.");
                        }

                        parsed.Positionals.Add(arg);
                        break;
                }
            }

            return parsed;
        }

        private static string Value(Queue<string> queue, string option)
        {
            if (queue.Count == 0) throw new ArgumentException($"{option} requires a value.");
            return queue.Dequeue();
        }

        private static int PositiveInt(string value, string option)
        {
            if (!int.TryParse(value, out var parsed) || parsed <= 0)
            {
                throw new ArgumentException($"{option} must be a positive integer, got '{value}'.");
            }

            return parsed;
        }
    }
}
