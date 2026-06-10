using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aegis.Backends;
using Aegis.Core;
using Aegis.Infrastructure;

namespace Aegis;

internal static partial class Program
{
    private const string NeedsRestartOrNudgeStatus = "needs-restart-or-nudge";

    private static async Task<int> RunCellCommand(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintCellHelp();
            WriteCellNextAction(NextCommandHintContext.General("Pick or create a cell, then launch linked worker sessions from it."));
            return 0;
        }

        var options = CellArgs.Parse(args);
        if (options.Help)
        {
            PrintCellHelp();
            WriteCellNextAction(NextCommandHintContext.General("Pick or create a cell, then launch linked worker sessions from it."));
            return 0;
        }

        var store = new FileCellStore();
        if (options.Positionals is ["serve", ..])
        {
            var host = string.IsNullOrWhiteSpace(options.Host) ? CellDefaultHost : options.Host;
            var port = options.Port ?? CellDefaultPort;
            WriteCellNextAction(NextCommandHintContext.General($"Open http://{host}:{port}/ or keep the observer running while agents update cells."));
            return await CellServe(store, options);
        }

        return options.Positionals switch
        {
            ["create", ..] => await CellCreate(store, options),
            ["fork", ..] => await CellFork(store, options),
            ["list", ..] => await CellList(store, options),
            ["show", ..] => await CellShow(store, options),
            ["brief", ..] => await CellBrief(store, options),
            ["launch", ..] => await CellLaunch(store, options),
            ["supervise", ..] => await CellSupervise(store, options),
            ["serve", ..] => await CellServe(store, options),
            ["store", "info", ..] => await CellStoreInfo(store),
            ["store", "export", ..] => await CellStoreExport(store, options),
            ["store", "import", ..] => await CellStoreImport(store, options),
            ["update", ..] => await CellMissionUpdate(store, options),
            ["mission", "update", ..] => await CellMissionUpdate(store, options),
            ["stream", "add", ..] => await CellStreamAdd(store, options),
            ["stream", "update", ..] => await CellStreamUpdate(store, options),
            ["stream", "delete", ..] => await CellStreamDelete(store, options),
            ["stream", "remove", ..] => await CellStreamDelete(store, options),
            ["session", "link", ..] => await CellSessionLink(store, options),
            ["session", "run", ..] => await CellSessionRun(store, options),
            ["session", "sync", ..] => await CellSessionSync(store, options),
            ["session", "update", ..] => await CellSessionUpdate(store, options),
            ["session", "archive", ..] => await CellSessionArchive(store, options),
            ["session", "handoff", ..] => await CellSessionHandoff(store, options),
            ["session", "blocker", "set", ..] => await CellSessionBlocker(store, options),
            ["session", "verify", ..] => await CellVerificationAdd(store, options),
            ["evidence", "add", ..] => await CellEvidenceAdd(store, options),
            ["evidence", "remove", ..] => await CellEvidenceRemove(store, options),
            ["verification", "add", ..] => await CellVerificationAdd(store, options),
            _ => Fail($"Unknown cell command '{string.Join(' ', options.Positionals)}'. Run `aegis help cell`.")
        };
    }

    private static async Task<int> CellCreate(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = new CellMissionRecord
        {
            Id = options.MissionId ?? NewCellId("cell"),
            Title = Require(options.Title, "--title"),
            Intent = options.Intent,
            Status = options.Status ?? "planned",
            ParentCellId = options.ParentCellId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            NextAction = options.NextAction,
            Events =
            [
                new CellEventRecord
                {
                    AtUtc = now,
                    Type = "created",
                    Summary = "Cell created."
                }
            ]
        };

        await store.SaveMissionAsync(mission);
        if (!string.IsNullOrWhiteSpace(options.ParentCellId))
        {
            await LinkChildCell(store, options.ParentCellId, mission.Id, now);
        }

        WriteCellJson(
            JsonSerializer.SerializeToNode(mission, JsonOptions),
            NextCommandHintContext.ForMission(mission.Id, "Add streams, fork child cells, or launch linked worker sessions from this cell."));
        return 0;
    }

    private static async Task<int> CellFork(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var parent = await RequireMission(store, options.MissionId);
        var child = new CellMissionRecord
        {
            Id = NewCellId("cell"),
            Title = Require(options.Title, "--title"),
            Intent = options.Intent,
            Status = options.Status ?? "planned",
            ParentCellId = parent.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            NextAction = options.NextAction,
            Events =
            [
                new CellEventRecord
                {
                    AtUtc = now,
                    Type = "forked",
                    Summary = $"Forked from parent cell {parent.Id}."
                }
            ]
        };

        await store.SaveMissionAsync(child);
        await LinkChildCell(store, parent.Id, child.Id, now);
        WriteCellJson(
            JsonSerializer.SerializeToNode(child, JsonOptions),
            NextCommandHintContext.ForMission(child.Id, "Add streams or launch linked worker sessions from this child cell."));
        return 0;
    }

    private static async Task<int> CellList(ICellStore store, CellArgs options)
    {
        var missions = await store.GetMissionsAsync();
        var ordered = missions.OrderByDescending(item => item.UpdatedAtUtc).ToArray();
        if (IsMarkdown(options.Format))
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Cells");
            builder.AppendLine();
            foreach (var mission in ordered)
            {
                builder.AppendLine($"- `{mission.Id}` - {mission.Title} ({mission.Status})");
            }

            await WriteCellOutput(WithInlineHints(builder.ToString(), NextCommandHintContext.General("Pick a cell to inspect or create a new one."), options), options);
            WriteCellNextAction(NextCommandHintContext.General("Pick a cell to inspect or create a new one."));
            return 0;
        }

        WriteCellJson(
            JsonSerializer.SerializeToNode(ordered, JsonOptions),
            NextCommandHintContext.General("Pick a cell to inspect or create a new one."));
        return 0;
    }

    private static async Task<int> CellMissionUpdate(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var missionId = Require(options.MissionId, "--cell");
        var updated = await store.UpdateMissionAsync(missionId, mission =>
        {
            var events = mission.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "cellUpdated",
                Summary = "Cell metadata updated."
            });

            return mission with
            {
                Title = options.Title ?? mission.Title,
                Intent = options.Intent ?? mission.Intent,
                Status = options.Status ?? mission.Status,
                NextAction = options.NextAction ?? mission.NextAction,
                Events = events,
                UpdatedAtUtc = now
            };
        });

        WriteCellJson(
            JsonSerializer.SerializeToNode(updated, JsonOptions),
            NextCommandHintContext.ForMission(updated.Id, "Inspect the cell and launch or supervise the next linked worker."));
        return 0;
    }

    private static async Task<int> CellStreamAdd(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var streamId = options.StreamId ?? NewCellId("stream");
        var workstream = new CellWorkstreamRecord
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

        await store.SaveWorkstreamAsync(workstream);
        await store.UpdateMissionAsync(mission.Id, current =>
        {
            var missionStreams = current.WorkstreamIds.ToList();
            AddUnique(missionStreams, streamId);
            var edges = current.Edges.ToList();
            foreach (var dependency in options.DependsOn)
            {
                if (!edges.Any(edge =>
                        string.Equals(edge.FromId, streamId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(edge.ToId, dependency, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(edge.Kind, "dependsOn", StringComparison.OrdinalIgnoreCase)))
                {
                    edges.Add(new CellEdgeRecord
                    {
                        FromId = streamId,
                        ToId = dependency,
                        Kind = "dependsOn"
                    });
                }
            }

            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "workstreamAdded",
                Summary = $"Added workstream '{workstream.Name}'."
            });

            return current with
            {
                WorkstreamIds = missionStreams,
                Edges = edges,
                Events = events,
                Status = current.Status == "planned" ? "in-progress" : current.Status,
                UpdatedAtUtc = now
            };
        });

        WriteCellJson(
            JsonSerializer.SerializeToNode(workstream, JsonOptions),
            NextCommandHintContext.ForWorkstream(workstream, "Launch a linked worker for the new stream."));
        return 0;
    }

    private static async Task<int> CellStreamUpdate(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var current = await RequireWorkstream(store, options.StreamId);
        EnsureMissionOwnsWorkstream(mission, current);

        var updated = await store.UpdateWorkstreamAsync(current.Id, stream => stream with
        {
            Name = options.Name ?? stream.Name,
            Role = options.Role ?? stream.Role,
            Target = options.Target ?? stream.Target,
            ClonePath = options.ClonePath is null ? stream.ClonePath : NormalizeOptionalPath(options.ClonePath),
            SourceRepoPath = options.SourceRepoPath is null ? stream.SourceRepoPath : NormalizeOptionalPath(options.SourceRepoPath),
            Branch = options.Branch ?? stream.Branch,
            Status = options.Status ?? stream.Status,
            IntegrationAction = options.IntegrationAction ?? stream.IntegrationAction,
            UpdatedAtUtc = now
        });

        await store.UpdateMissionAsync(mission.Id, item =>
        {
            var streams = item.WorkstreamIds.ToList();
            AddUnique(streams, updated.Id);
            var events = item.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "workstreamUpdated",
                Summary = $"Updated workstream '{updated.Name}'."
            });

            return item with
            {
                WorkstreamIds = streams,
                Events = events,
                UpdatedAtUtc = now
            };
        });

        WriteCellJson(
            JsonSerializer.SerializeToNode(updated, JsonOptions),
            NextCommandHintContext.ForWorkstream(updated, "Launch, sync, or supervise the worker attached to this stream."));
        return 0;
    }

    private static async Task<int> CellStreamDelete(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var stream = await RequireWorkstream(store, options.StreamId);
        EnsureMissionOwnsWorkstream(mission, stream);
        if (stream.SessionIds.Count > 0 && !options.Force)
        {
            return Fail($"Workstream '{stream.Id}' has linked sessions. Pass --force to remove the stream record anyway.");
        }

        await store.DeleteWorkstreamAsync(stream.Id);
        var updatedMission = await store.UpdateMissionAsync(mission.Id, item =>
        {
            var streams = item.WorkstreamIds
                .Where(id => !string.Equals(id, stream.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var edges = item.Edges
                .Where(edge =>
                    !string.Equals(edge.FromId, stream.Id, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(edge.ToId, stream.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var events = item.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "workstreamDeleted",
                Summary = $"Deleted workstream '{stream.Name}'."
            });

            return item with
            {
                WorkstreamIds = streams,
                Edges = edges,
                Events = events,
                UpdatedAtUtc = now
            };
        });

        WriteCellJson(new JsonObject
        {
            ["cellID"] = updatedMission.Id,
            ["missionID"] = updatedMission.Id,
            ["streamID"] = stream.Id,
            ["deleted"] = true
        }, NextCommandHintContext.ForMission(updatedMission.Id, "Inspect the cell and decide which remaining stream needs ownership."));
        return 0;
    }

    private static async Task<int> CellSessionLink(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var workstream = await RequireWorkstream(store, options.StreamId);
        EnsureMissionOwnsWorkstream(mission, workstream);

        var sessionId = Require(options.SessionId, "--session");
        var backend = options.Backend is null
            ? InferBackendFromSessionId(sessionId) ?? "codex"
            : NormalizeLinkedBackend(options.Backend);
        var session = new CellAgentSessionRecord
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
            Agent = options.Agent,
            Directory = NormalizeOptionalPath(options.Directory) ?? workstream.ClonePath,
            Status = options.Status ?? "linked",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Events =
            [
                new CellEventRecord
                {
                    AtUtc = now,
                    Type = "linked",
                    Summary = "Existing session linked to cell."
                }
            ]
        };

        await SaveSessionAttachment(store, mission, workstream, session, now, "Session linked.");
        WriteCellJson(
            JsonSerializer.SerializeToNode(session, JsonOptions),
            NextCommandHintContext.ForSession(session, "Sync or supervise the linked session so the cell reflects current worker state."));
        return 0;
    }

    private static async Task<int> CellSessionRun(ICellStore store, CellArgs options)
    {
        var mission = await RequireMission(store, options.MissionId);
        var workstream = await RequireWorkstream(store, options.StreamId);
        EnsureMissionOwnsWorkstream(mission, workstream);

        var resolved = ResolveCellProfile(options);
        var validationError = ValidateCellSessionRun(resolved, options);
        if (validationError is not null)
        {
            return Fail(validationError);
        }

        var prompt = await ReadCellPrompt(options);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Fail("Prompt is required. Use --prompt or --prompt-file.");
        }

        var outcome = await RunCellSession(store, mission, workstream, options, resolved, prompt, options.Async, options.Wait);
        if (outcome.Blocker is not null)
        {
            return Fail(outcome.Blocker.Evidence is null
                ? outcome.Blocker.Summary
                : $"{outcome.Blocker.Summary}: {outcome.Blocker.Evidence}");
        }

        WriteCellJson(new JsonObject
        {
            ["cellID"] = mission.Id,
            ["missionID"] = mission.Id,
            ["workstreamID"] = workstream.Id,
            ["sessionID"] = outcome.Session.Id,
            ["displayName"] = outcome.Session.DisplayName,
            ["backend"] = outcome.Session.Backend,
            ["backendSessionID"] = outcome.Session.BackendSessionId,
            ["requestedBackend"] = options.Backend,
            ["provider"] = outcome.Session.Provider,
            ["model"] = outcome.Session.Model,
            ["variant"] = outcome.Session.Variant,
            ["agent"] = outcome.Session.Agent,
            ["directory"] = outcome.Session.Directory,
            ["title"] = outcome.Session.Title,
            ["status"] = outcome.Session.Status,
            ["summary"] = outcome.Session.FinalHandoff?.Text,
            ["nextCommands"] = JsonSerializer.SerializeToNode(BuildSessionRunNextCommands(mission.Id, outcome.Session), JsonOptions)
        }, NextCommandHintContext.ForSession(outcome.Session, "Supervise the cell or inspect the worker handoff."));
        return 0;
    }

    private static async Task<CellSessionRunOutcome> RunCellSession(
        ICellStore store,
        CellMissionRecord mission,
        CellWorkstreamRecord workstream,
        CellArgs options,
        ResolvedAgentProfile resolved,
        string prompt,
        bool async,
        bool wait)
    {
        var now = DateTimeOffset.UtcNow;
        using var http = CreateHttpClient(options.Server ?? DefaultServer);
        var client = new OpenCodeClient(http);
        var backend = CreateBackend(resolved.Backend, client);
        var commands = new BackendCommandService(backend, new SessionRegistryService(new FileSessionRegistry()));
        var directory = NormalizeOptionalPath(options.Directory) ?? workstream.ClonePath;
        var sourceKind = !string.IsNullOrWhiteSpace(options.PromptFile)
            ? PromptSourceKind.File
            : Console.IsInputRedirected && string.IsNullOrWhiteSpace(options.Prompt)
                ? PromptSourceKind.Stdin
                : PromptSourceKind.Inline;
        var request = new PromptRequest(
            Text: prompt,
            SourceKind: sourceKind,
            SourceLocation: options.PromptFile,
            ModelProvider: resolved.ModelProvider,
            Model: resolved.Model,
            Variant: resolved.Variant,
            SummaryMarker: options.SummaryMarker,
            Directory: directory,
            Agent: resolved.Agent,
            System: resolved.System,
            NoReply: options.NoReply,
            Raw: options.Raw,
            Options: BuildCopilotOptions(options));

        CellAgentSessionRecord? attachedSession = null;
        BackendAskResult result;
        try
        {
            result = await commands.AskAsync(new BackendAskRequest(
                SessionId: null,
                Title: options.Title ?? workstream.Name,
                ParentSessionId: null,
                Directory: directory,
                Prompt: request,
                Async: async,
                Wait: wait,
                Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
                SessionCreated: async sessionRecord =>
                {
                    attachedSession = new CellAgentSessionRecord
                    {
                        Id = sessionRecord.SessionId,
                        MissionId = mission.Id,
                        WorkstreamId = workstream.Id,
                        DisplayName = options.DisplayName ?? GenerateDisplayName(sessionRecord.SessionId),
                        Title = options.Title ?? workstream.Name,
                        Role = options.Role ?? workstream.Role,
                        Backend = backend.Kind.ToOptionValue(),
                        BackendSessionId = sessionRecord.BackendSessionId,
                        Provider = resolved.ModelProvider,
                        Model = resolved.Model,
                        Variant = resolved.Variant,
                        Agent = resolved.Agent,
                        Directory = directory,
                        TimeoutSeconds = options.TimeoutSeconds,
                        Status = "running",
                        CreatedAtUtc = sessionRecord.CreatedAtUtc,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Events =
                        [
                            new CellEventRecord
                            {
                                AtUtc = DateTimeOffset.UtcNow,
                                Type = "promptStarting",
                                Summary = $"Backend session created; prompt starting through {backend.Kind.ToOptionValue()}."
                            }
                        ],
                        Messages = EnsurePromptMessage(prompt, now, [])
                    };

                    await SaveSessionAttachment(store, mission, workstream, attachedSession, DateTimeOffset.UtcNow, "Session run started.");
                }));
        }
        catch (Exception ex) when (attachedSession is not null)
        {
            return await SaveSessionRunFailure(store, mission, workstream, attachedSession, "Session run failed after backend session creation.", ex.Message);
        }

        try
        {
            var states = await commands.GetStatusAsync(result.Session.SessionId);
            var state = states.FirstOrDefault();
            var summary = result.Summary;
            if (summary is null && result.PostResult.IsSuccess && state?.HasFreshSummary == true)
            {
                summary = await commands.GetLastSummaryAsync(result.Session.SessionId, options.SummaryMarker);
            }

            IReadOnlyList<BackendMessage> messages = result.PostResult.IsSuccess
                ? await commands.GetMessagesAsync(result.Session.SessionId, options.MessageLimit)
                : Array.Empty<BackendMessage>();
            var messageRecords = EnsurePromptMessage(prompt, now, ToCellMessages(messages));
            messageRecords = attachedSession is null
                ? messageRecords
                : MergeCellMessages(attachedSession.Messages, messageRecords);
            var observations = attachedSession?.StatusObservations.ToList() ?? [];
            if (state is not null)
            {
                observations.Add(ToCellStatusObservation(state, DateTimeOffset.UtcNow));
            }

            var status = summary is not null
                ? "handoff"
                : async && !wait && (state is null || !IsActiveStatus(state.EffectiveStatus))
                    ? "queued"
                    : state?.EffectiveStatus ?? "waiting";
            CellBlockerRecord? blocker = null;
            if (!result.PostResult.IsSuccess)
            {
                status = "blocked";
                blocker = new CellBlockerRecord
                {
                    AtUtc = now,
                    Summary = result.PostResult.Message ?? "Backend prompt failed.",
                    Evidence = result.PostResult.Error
                };
            }

            var events = attachedSession?.Events.ToList() ?? [];
            events.Add(new CellEventRecord
            {
                AtUtc = DateTimeOffset.UtcNow,
                Type = result.PostResult.IsSuccess ? "promptSent" : "promptFailed",
                Summary = result.PostResult.IsSuccess
                    ? $"Prompt sent through {backend.Kind.ToOptionValue()}."
                    : result.PostResult.Message ?? "Backend prompt failed."
            });
            if (summary is not null)
            {
                events.Add(new CellEventRecord
                {
                    AtUtc = DateTimeOffset.UtcNow,
                    Type = "finalHandoffFound",
                    Summary = "Worker returned a final handoff."
                });
            }

            var session = new CellAgentSessionRecord
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
                Agent = resolved.Agent,
                Directory = directory,
                TimeoutSeconds = options.TimeoutSeconds,
                Status = status,
                CreatedAtUtc = attachedSession?.CreatedAtUtc ?? now,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Events = events,
                Messages = messageRecords,
                StatusObservations = observations,
                FinalHandoff = summary is null
                    ? null
                    : new CellHandoffRecord
                    {
                        AtUtc = DateTimeOffset.UtcNow,
                        Text = summary.Text
                    },
                Blocker = blocker
            };

            await SaveSessionRunUpdate(store, mission, workstream, session, DateTimeOffset.UtcNow, attachedSession is null);

            return new CellSessionRunOutcome(session, blocker);
        }
        catch (Exception ex) when (attachedSession is not null)
        {
            return await SaveSessionRunFailure(store, mission, workstream, attachedSession, "Session run sync failed after backend prompt.", ex.Message);
        }
    }

    private static async Task<int> CellSessionSync(ICellStore store, CellArgs options)
    {
        if (options.All)
        {
            var mission = await RequireMission(store, options.MissionId);
            var sessions = await store.GetAgentSessionsAsync(mission.Id);
            var results = new JsonArray();
            foreach (var session in sessions)
            {
                try
                {
                    var synced = await SyncCellSession(store, session, options);
                    results.Add(JsonSerializer.SerializeToNode(synced, JsonOptions));
                }
                catch (Exception ex)
                {
                    var now = DateTimeOffset.UtcNow;
                    await store.UpdateAgentSessionAsync(session.Id, current =>
                    {
                        var events = current.Events.ToList();
                        events.Add(new CellEventRecord
                        {
                            AtUtc = now,
                            Type = "syncFailed",
                            Summary = ex.Message
                        });

                        return current with
                        {
                            Status = "sync-failed",
                            Events = events,
                            Blocker = new CellBlockerRecord
                            {
                                AtUtc = now,
                                Summary = "Session sync failed.",
                                Evidence = ex.Message
                            },
                            UpdatedAtUtc = now
                        };
                    });
                    results.Add(new JsonObject
                    {
                        ["sessionID"] = session.Id,
                        ["status"] = "sync-failed",
                        ["error"] = ex.Message
                    });
                }
            }

            WriteCellJson(
                results,
                NextCommandHintContext.ForMission(mission.Id, "Review sync results, then supervise again or inspect sessions needing restart/nudge."));
            return 0;
        }

        var updated = await SyncCellSession(store, await RequireAgentSession(store, options.SessionId), options);
        WriteCellJson(
            JsonSerializer.SerializeToNode(updated, JsonOptions),
            NextCommandHintContext.ForSession(updated, "Inspect the latest handoff, blocker, or mission state."));
        return 0;
    }

    private static async Task<CellAgentSessionRecord> SyncCellSession(
        ICellStore store,
        CellAgentSessionRecord session,
        CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        if (!BackendKindExtensions.TryParse(session.Backend, out var backendKind))
        {
            return await SkipExternalSessionSync(store, session, now);
        }

        using var http = CreateHttpClient(options.Server ?? DefaultServer);
        var backend = CreateBackend(backendKind, new OpenCodeClient(http));
        var backendSession = ToBackendSessionRecord(session, backendKind);
        var allMessages = await backend.GetMessagesAsync(backendSession, 0);
        var anchorMessageIndex = LatestUserMessageIndex(allMessages);
        var state = await backend.GetSessionStateAsync(backendSession, anchorMessageIndex);
        var summary = await backend.ExtractSummaryAsync(backendSession, options.SummaryMarker, anchorMessageIndex);
        var messages = LimitBackendMessages(allMessages, options.MessageLimit);
        var incomingMessages = ToCellMessages(messages);

        var updated = await store.UpdateAgentSessionAsync(session.Id, current =>
        {
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "synced",
                Summary = state is null ? "No status snapshot returned." : $"Status observed as {state.EffectiveStatus}."
            });

            var observations = current.StatusObservations.ToList();
            if (state is not null)
            {
                observations.Add(ToCellStatusObservation(state, now));
            }

            var needsRestartOrNudge = summary is null
                                       && current.FinalHandoff is null
                                       && current.Blocker is null
                                       && ShouldMarkNeedsRestartOrNudge(current, state, now);
            if (needsRestartOrNudge)
            {
                events.Add(new CellEventRecord
                {
                    AtUtc = now,
                    Type = "restartOrNudgeNeeded",
                    Summary = BuildMissingHandoffRecoverySummary(state, allMessages, anchorMessageIndex, options.SummaryMarker)
                });
            }

            var keepQueued = summary is null
                             && current.FinalHandoff is null
                             && current.Blocker is null
                             && !needsRestartOrNudge
                             && ShouldKeepAsyncSessionQueued(current, state, now);

            return current with
            {
                Status = summary is not null
                    ? "handoff"
                    : needsRestartOrNudge
                        ? NeedsRestartOrNudgeStatus
                        : keepQueued
                            ? current.Status
                            : state?.EffectiveStatus ?? current.Status,
                UpdatedAtUtc = now,
                Events = events,
                Messages = MergeCellMessages(current.Messages, incomingMessages),
                StatusObservations = observations,
                FinalHandoff = summary is null ? current.FinalHandoff : new CellHandoffRecord
                {
                    AtUtc = now,
                    Text = summary.Text
                },
                Blocker = summary is not null || needsRestartOrNudge ? null : current.Blocker
            };
        });
        await UpdateSessionParents(store, updated, now, "sessionSynced", state is null ? "No status snapshot returned." : $"Status observed as {state.EffectiveStatus}.");
        return updated;
    }

    private static SessionRecord ToBackendSessionRecord(CellAgentSessionRecord session, BackendKind backendKind)
    {
        var backendSessionId = string.IsNullOrWhiteSpace(session.BackendSessionId)
            ? session.Id
            : session.BackendSessionId;
        return new SessionRecord(
            SessionId: session.Id,
            Backend: backendKind,
            BackendSessionId: backendSessionId,
            CreatedAtUtc: session.CreatedAtUtc,
            Directory: NormalizeOptionalPath(session.Directory));
    }

    private static async Task<int> CellSessionUpdate(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await RequireAgentSession(store, options.SessionId);
        var backend = options.Backend is null ? null : NormalizeLinkedBackend(options.Backend);
        var updated = await store.UpdateAgentSessionAsync(session.Id, current =>
        {
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "updated",
                Summary = "Session metadata updated."
            });

            return current with
            {
                DisplayName = options.DisplayName ?? current.DisplayName,
                Title = options.Title ?? current.Title,
                Role = options.Role ?? current.Role,
                Backend = backend ?? current.Backend,
                BackendSessionId = options.BackendSessionId ?? current.BackendSessionId,
                Provider = options.Provider ?? current.Provider,
                Model = options.Model ?? current.Model,
                Variant = options.Variant ?? current.Variant,
                Agent = options.Agent ?? current.Agent,
                Directory = options.Directory is null ? current.Directory : NormalizeOptionalPath(options.Directory),
                Status = options.Status ?? current.Status,
                Events = events,
                UpdatedAtUtc = now
            };
        });

        WriteCellJson(
            JsonSerializer.SerializeToNode(updated, JsonOptions),
            NextCommandHintContext.ForSession(updated, "Sync or supervise the updated session."));
        return 0;
    }

    private static async Task<int> CellSessionArchive(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await RequireAgentSession(store, options.SessionId);
        var summary = string.IsNullOrWhiteSpace(options.Summary)
            ? "Session archived."
            : options.Summary;
        var updated = await store.UpdateAgentSessionAsync(session.Id, current =>
        {
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "archived",
                Summary = summary
            });

            return current with
            {
                Status = "archived",
                Events = events,
                UpdatedAtUtc = now
            };
        });

        await UpdateSessionParents(store, updated, now, "sessionArchived", summary);
        WriteCellJson(
            JsonSerializer.SerializeToNode(updated, JsonOptions),
            NextCommandHintContext.ForSession(updated, "Inspect the mission and relaunch only if this archived session still has unfinished work."));
        return 0;
    }

    private static async Task<int> CellSessionHandoff(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var text = await ReadTextOption(options, "--summary or --file");
        var session = await RequireAgentSession(store, options.SessionId);
        var updated = await store.UpdateAgentSessionAsync(session.Id, current =>
        {
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "handoffRecorded",
                Summary = "Final handoff recorded."
            });

            return current with
            {
                Status = options.Status ?? "handoff",
                FinalHandoff = new CellHandoffRecord
                {
                    AtUtc = now,
                    Text = text
                },
                Events = events,
                UpdatedAtUtc = now
            };
        });

        await UpdateSessionParents(store, updated, now, "sessionHandoffRecorded", "Session handoff recorded.");
        WriteCellJson(
            JsonSerializer.SerializeToNode(updated, JsonOptions),
            NextCommandHintContext.ForSession(updated, "Integrate the handoff, then record evidence or verification on the map."));
        return 0;
    }

    private static async Task<int> CellSessionBlocker(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var summary = Require(options.Summary, "--summary");
        var session = await RequireAgentSession(store, options.SessionId);
        var updated = await store.UpdateAgentSessionAsync(session.Id, current =>
        {
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "blockerRecorded",
                Summary = summary
            });

            return current with
            {
                Status = options.Status ?? "blocked",
                Blocker = new CellBlockerRecord
                {
                    AtUtc = now,
                    Summary = summary,
                    Evidence = options.EvidenceText
                },
                Events = events,
                UpdatedAtUtc = now
            };
        });

        await UpdateSessionParents(store, updated, now, "sessionBlockerRecorded", summary);
        WriteCellJson(
            JsonSerializer.SerializeToNode(updated, JsonOptions),
            NextCommandHintContext.ForSession(updated, "Resolve the blocker or launch a replacement worker from the same stream."));
        return 0;
    }

    private static async Task<int> CellVerificationAdd(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await RequireAgentSession(store, options.SessionId);
        var verification = new CellVerificationRecord
        {
            AtUtc = now,
            Kind = Require(options.Kind, "--kind"),
            Result = Require(options.Result, "--result"),
            Summary = options.Summary
        };

        var updated = await store.UpdateAgentSessionAsync(session.Id, current =>
        {
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "verificationRecorded",
                Summary = $"{verification.Kind}: {verification.Result}"
            });

            var verifications = current.Verification.ToList();
            verifications.Add(verification);

            return current with
            {
                Verification = verifications,
                Status = options.Status ?? current.Status,
                Events = events,
                UpdatedAtUtc = now
            };
        });

        await UpdateSessionParents(store, updated, now, "sessionVerificationRecorded", $"{verification.Kind}: {verification.Result}");
        WriteCellJson(
            JsonSerializer.SerializeToNode(updated, JsonOptions),
            NextCommandHintContext.ForSession(updated, "Use the verification result to integrate, relaunch, or close the stream."));
        return 0;
    }

    private static async Task<int> CellEvidenceAdd(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = await RequireMission(store, options.MissionId);
        var evidence = new CellEvidenceRecord
        {
            Id = NewCellId("evidence"),
            Kind = options.Kind ?? "note",
            Path = NormalizeOptionalPath(options.Path),
            Summary = options.Summary,
            AddedAtUtc = now
        };

        if (!string.IsNullOrWhiteSpace(options.StreamId))
        {
            var workstream = await RequireWorkstream(store, options.StreamId);
            EnsureMissionOwnsWorkstream(mission, workstream);
            await store.UpdateWorkstreamAsync(workstream.Id, current =>
            {
                var evidenceList = current.Evidence.ToList();
                evidenceList.Add(evidence);
                return current with
                {
                    Evidence = evidenceList,
                    UpdatedAtUtc = now
                };
            });
        }

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            var session = await RequireAgentSession(store, options.SessionId);
            EnsureMissionOwnsSession(mission, session);
            await store.UpdateAgentSessionAsync(session.Id, current =>
            {
                var evidenceList = current.Evidence.ToList();
                evidenceList.Add(evidence);
                return current with
                {
                    Evidence = evidenceList,
                    UpdatedAtUtc = now
                };
            });
        }

        if (string.IsNullOrWhiteSpace(options.StreamId) && string.IsNullOrWhiteSpace(options.SessionId))
        {
            await store.UpdateMissionAsync(mission.Id, current =>
            {
                var evidenceList = current.Evidence.ToList();
                evidenceList.Add(evidence);
                return current with
                {
                    Evidence = evidenceList,
                    UpdatedAtUtc = now
                };
            });
        }

        WriteCellJson(
            JsonSerializer.SerializeToNode(evidence, JsonOptions),
            new NextCommandHintContext(
                MissionId: mission.Id,
                StreamId: options.StreamId,
                SessionId: options.SessionId,
                Backend: null,
                Directory: null,
                Role: null,
                SuggestedNextAction: "Inspect the mission and decide whether to supervise, verify, or launch the next worker."));
        return 0;
    }

    private static async Task<int> CellEvidenceRemove(ICellStore store, CellArgs options)
    {
        var now = DateTimeOffset.UtcNow;
        var evidenceId = Require(options.EvidenceId, "--evidence-id");
        var mission = await RequireMission(store, options.MissionId);

        if (!string.IsNullOrWhiteSpace(options.StreamId))
        {
            var workstream = await RequireWorkstream(store, options.StreamId);
            EnsureMissionOwnsWorkstream(mission, workstream);
            var updated = await store.UpdateWorkstreamAsync(workstream.Id, current => current with
            {
                Evidence = current.Evidence
                    .Where(evidence => !string.Equals(evidence.Id, evidenceId, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                UpdatedAtUtc = now
            });
            WriteCellJson(
                JsonSerializer.SerializeToNode(updated, JsonOptions),
                NextCommandHintContext.ForWorkstream(updated, "Inspect the stream and replace or refresh evidence if needed."));
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            var session = await RequireAgentSession(store, options.SessionId);
            EnsureMissionOwnsSession(mission, session);
            var updated = await store.UpdateAgentSessionAsync(session.Id, current => current with
            {
                Evidence = current.Evidence
                    .Where(evidence => !string.Equals(evidence.Id, evidenceId, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                UpdatedAtUtc = now
            });
            WriteCellJson(
                JsonSerializer.SerializeToNode(updated, JsonOptions),
                NextCommandHintContext.ForSession(updated, "Inspect the session and replace or refresh evidence if needed."));
            return 0;
        }

        var updatedMission = await store.UpdateMissionAsync(mission.Id, current => current with
        {
            Evidence = current.Evidence
                .Where(evidence => !string.Equals(evidence.Id, evidenceId, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            UpdatedAtUtc = now
        });
        WriteCellJson(
            JsonSerializer.SerializeToNode(updatedMission, JsonOptions),
            NextCommandHintContext.ForMission(updatedMission.Id, "Inspect the mission and replace or refresh evidence if needed."));
        return 0;
    }

    private static async Task<int> CellShow(ICellStore store, CellArgs options)
    {
        var mission = await RequireMission(store, options.MissionId);
        var workstreams = await store.GetWorkstreamsAsync(mission.Id);
        var sessions = await store.GetAgentSessionsAsync(mission.Id);
        var bundle = new CellBundle(mission, workstreams, sessions);

        if (IsMarkdown(options.Format))
        {
            var context = NextCommandHintContext.ForMission(mission.Id, "Pick the next stream to launch, supervise, verify, or close.");
            await WriteCellOutput(WithInlineHints(BuildCellMarkdown(bundle), context, options), options);
            WriteCellNextAction(context);
            return 0;
        }

        if (options.Format.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            var context = NextCommandHintContext.ForMission(mission.Id, "Pick the next stream to launch, supervise, verify, or close.");
            await WriteCellOutput(BuildCellHtml(bundle), options);
            WriteCellNextAction(context);
            return 0;
        }

        WriteCellJson(
            JsonSerializer.SerializeToNode(bundle, JsonOptions),
            NextCommandHintContext.ForMission(mission.Id, "Pick the next stream to launch, supervise, verify, or close."));
        return 0;
    }

    private static async Task<int> CellBrief(ICellStore store, CellArgs options)
    {
        var mission = await RequireMission(store, options.MissionId);
        var workstream = await RequireWorkstream(store, options.StreamId);
        EnsureMissionOwnsWorkstream(mission, workstream);
        var sessions = await store.GetAgentSessionsAsync(mission.Id);
        var relevantSessions = sessions.Where(item => item.WorkstreamId == workstream.Id).ToArray();
        var brief = BuildDelegationBrief(mission, workstream, relevantSessions);
        await WriteCellOutput(brief, options);
        WriteCellNextAction(NextCommandHintContext.ForWorkstream(workstream, "Launch a linked worker with this brief, or supervise the stream if it is already running."));
        return 0;
    }

    private static async Task<int> CellLaunch(ICellStore store, CellArgs options)
    {
        var mission = await RequireMission(store, options.MissionId);
        var result = await LaunchCellMission(store, mission, options);
        WriteCellJson(
            ToLaunchJson(result),
            NextCommandHintContext.ForMission(mission.Id, result.FailureCount == 0
                ? "Supervise launched workers until idle, then inspect handoffs."
                : "Inspect launch failures and retry or update blocked streams."));
        return result.FailureCount == 0 ? 0 : 1;
    }

    private static async Task<CellLaunchResult> LaunchCellMission(
        ICellStore store,
        CellMissionRecord mission,
        CellArgs options)
    {
        var resolved = ResolveCellProfile(options);
        var extraPrompt = await ReadCellPrompt(options);
        var workstreams = OrderWorkstreams(mission, await store.GetWorkstreamsAsync(mission.Id));
        var sessions = (await store.GetAgentSessionsAsync(mission.Id)).ToList();
        var launched = new JsonArray();
        var skipped = new JsonArray();
        var eligibleCount = 0;
        var failureCount = 0;

        foreach (var workstream in workstreams)
        {
            var recordedStreamSessions = SessionsForWorkstream(workstream, sessions);
            var archivedStreamSessionIds = recordedStreamSessions
                .Where(session => IsArchivedStatus(session.Status))
                .Select(session => session.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var streamSessions = recordedStreamSessions
                .Where(session => !IsArchivedStatus(session.Status))
                .ToList();
            var existingSessionCount = streamSessions.Count + workstream.SessionIds
                .Count(id =>
                    !archivedStreamSessionIds.Contains(id)
                    && streamSessions.All(session => !string.Equals(session.Id, id, StringComparison.OrdinalIgnoreCase)));

            if (!options.IncludeComplete && IsCompleteStatus(workstream.Status))
            {
                skipped.Add(LaunchSkip(workstream, "complete"));
                continue;
            }

            if (!options.Force && existingSessionCount > 0)
            {
                skipped.Add(LaunchSkip(workstream, "session-exists"));
                continue;
            }

            eligibleCount++;
            var directory = NormalizeOptionalPath(options.Directory) ?? workstream.ClonePath;
            if (options.DryRun)
            {
                launched.Add(new JsonObject
                {
                    ["workstreamID"] = workstream.Id,
                    ["name"] = workstream.Name,
                    ["backend"] = resolved.Backend.ToOptionValue(),
                    ["directory"] = directory,
                    ["status"] = "planned"
                });
                continue;
            }

            try
            {
                var prompt = BuildLaunchPrompt(mission, workstream, streamSessions, extraPrompt);
                var outcome = await RunCellSession(
                    store,
                    mission,
                    workstream,
                    options,
                    resolved,
                    prompt,
                    async: BackendAvailabilityDetector.SupportsDetachedAsync(resolved.Backend),
                    wait: options.Wait);
                sessions.Add(outcome.Session);
                if (outcome.Blocker is not null)
                {
                    failureCount++;
                }

                launched.Add(new JsonObject
                {
                    ["workstreamID"] = workstream.Id,
                    ["name"] = workstream.Name,
                    ["sessionID"] = outcome.Session.Id,
                    ["backend"] = outcome.Session.Backend,
                    ["directory"] = outcome.Session.Directory,
                    ["status"] = outcome.Session.Status,
                    ["summary"] = outcome.Session.FinalHandoff?.Text,
                    ["error"] = outcome.Blocker?.Evidence ?? outcome.Blocker?.Summary
                });
            }
            catch (Exception ex)
            {
                failureCount++;
                launched.Add(new JsonObject
                {
                    ["workstreamID"] = workstream.Id,
                    ["name"] = workstream.Name,
                    ["backend"] = resolved.Backend.ToOptionValue(),
                    ["directory"] = directory,
                    ["status"] = "launch-failed",
                    ["error"] = ex.Message
                });
            }
        }

        return new CellLaunchResult(
            mission.Id,
            resolved.Backend.ToOptionValue(),
            options.DryRun,
            eligibleCount,
            launched.Count,
            skipped.Count,
            failureCount,
            launched,
            skipped);
    }

    private static async Task<int> CellSupervise(ICellStore store, CellArgs options)
    {
        var mission = await RequireMission(store, options.MissionId);
        var started = DateTimeOffset.UtcNow;
        var deadline = options.MaxDurationMinutes is null
            ? (DateTimeOffset?)null
            : started.AddMinutes(options.MaxDurationMinutes.Value);
        var repeatRequested = options.UntilIdle || options.MaxRuns is not null || options.MaxDurationMinutes is not null;
        var maxRuns = options.MaxRuns ?? (repeatRequested ? int.MaxValue : 1);
        var runs = new JsonArray();
        CellSupervisionCounts finalCounts = new(0, 0, 0, 0);

        for (var run = 1; run <= maxRuns; run++)
        {
            JsonObject? launchMissing = null;
            if (options.LaunchMissing)
            {
                launchMissing = ToLaunchJson(await LaunchCellMission(store, mission, options));
            }

            var sessions = (await store.GetAgentSessionsAsync(mission.Id)).ToList();
            var synced = new List<CellAgentSessionRecord>();
            var syncErrors = new JsonArray();

            foreach (var session in sessions)
            {
                if (options.DryRun)
                {
                    synced.Add(session);
                    continue;
                }

                try
                {
                    synced.Add(await SyncCellSession(store, session, options));
                }
                catch (Exception ex)
                {
                    syncErrors.Add(new JsonObject
                    {
                        ["sessionID"] = session.Id,
                        ["error"] = ex.Message
                    });
                    synced.Add(await MarkCellSessionSyncFailed(store, session, ex.Message));
                }
            }

            finalCounts = CountSupervisionStatuses(synced);
            var runResult = new JsonObject
            {
                ["run"] = run,
                ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["quiet"] = finalCounts.Quiet,
                ["active"] = finalCounts.Active,
                ["blocked"] = finalCounts.Blocked,
                ["handoff"] = finalCounts.Handoff,
                ["sessions"] = BuildSupervisionSessionsJson(synced),
                ["syncErrors"] = syncErrors
            };
            if (launchMissing is not null)
            {
                runResult["launchMissing"] = launchMissing;
            }

            runs.Add(runResult);

            if (!repeatRequested) break;
            if (options.UntilIdle && finalCounts.Active == 0) break;
            if (run >= maxRuns) break;
            if (deadline is not null && DateTimeOffset.UtcNow >= deadline.Value) break;

            var delay = TimeSpan.FromSeconds(options.IntervalSeconds);
            if (deadline is not null)
            {
                var remaining = deadline.Value - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                if (delay > remaining) delay = remaining;
            }

            await Task.Delay(delay);
        }

        WriteCellJson(new JsonObject
        {
            ["cellID"] = mission.Id,
            ["missionID"] = mission.Id,
            ["quiet"] = finalCounts.Quiet,
            ["active"] = finalCounts.Active,
            ["blocked"] = finalCounts.Blocked,
            ["handoff"] = finalCounts.Handoff,
            ["runs"] = runs
        }, NextCommandHintContext.ForMission(mission.Id, finalCounts.Active > 0
            ? "Continue supervision until active workers are idle."
            : "Inspect handoffs, blockers, sessions needing restart/nudge, and evidence before closing or launching more work."));
        return 0;
    }

    private static async Task<int> CellStoreInfo(ICellStore store)
    {
        var missions = await store.GetMissionsAsync();
        var workstreams = await store.GetWorkstreamsAsync();
        var sessions = await store.GetAgentSessionsAsync();
        var directory = (store as FileCellStore)?.DirectoryPath;
        var exists = directory is not null && Directory.Exists(directory);

        WriteCellJson(new JsonObject
        {
            ["provider"] = "json-directory",
            ["directory"] = directory,
            ["exists"] = exists,
            ["cells"] = missions.Count,
            ["missions"] = missions.Count,
            ["workstreams"] = workstreams.Count,
            ["sessions"] = sessions.Count,
            ["jsonFiles"] = exists ? CountJsonFiles(directory!) : 0,
            ["bytes"] = exists ? SumFileBytes(directory!) : 0
        }, NextCommandHintContext.General("List or show a cell from this store."));
        return 0;
    }

    private static async Task<int> CellStoreExport(ICellStore store, CellArgs options)
    {
        var snapshot = new CellStoreSnapshot
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Missions = (await store.GetMissionsAsync()).OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            Workstreams = (await store.GetWorkstreamsAsync()).OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            Sessions = (await store.GetAgentSessionsAsync()).OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList()
        };
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await WriteCellOutput(json + Environment.NewLine, options);
        WriteCellNextAction(NextCommandHintContext.General("Import this snapshot into another store, or inspect a cell from the exported records."));
        return 0;
    }

    private static async Task<int> CellStoreImport(ICellStore store, CellArgs options)
    {
        var snapshot = await ReadCellSnapshot(options);
        var conflicts = await FindSnapshotConflicts(store, snapshot);
        if (conflicts.Count > 0 && !options.Force)
        {
            WriteCellJson(new JsonObject
            {
                ["imported"] = false,
                ["conflicts"] = conflicts,
                ["message"] = "Snapshot contains records that already exist. Pass --force to overwrite them."
            }, NextCommandHintContext.General("Review import conflicts; rerun with --force only when overwriting is intended."));
            return 1;
        }

        if (options.DryRun)
        {
            WriteCellJson(new JsonObject
            {
                ["imported"] = false,
                ["dryRun"] = true,
                ["cells"] = snapshot.Missions.Count,
                ["missions"] = snapshot.Missions.Count,
                ["workstreams"] = snapshot.Workstreams.Count,
                ["sessions"] = snapshot.Sessions.Count,
                ["conflicts"] = conflicts
            }, NextCommandHintContext.General("If the dry-run looks correct, rerun store import without --dry-run."));
            return 0;
        }

        foreach (var mission in snapshot.Missions)
        {
            ValidateRecordId(mission.Id, "cell");
            await store.SaveMissionAsync(mission);
        }

        foreach (var workstream in snapshot.Workstreams)
        {
            ValidateRecordId(workstream.Id, "workstream");
            await store.SaveWorkstreamAsync(workstream);
        }

        foreach (var session in snapshot.Sessions)
        {
            ValidateRecordId(session.Id, "session");
            await store.SaveAgentSessionAsync(session);
        }

        WriteCellJson(new JsonObject
        {
            ["imported"] = true,
            ["cells"] = snapshot.Missions.Count,
            ["missions"] = snapshot.Missions.Count,
            ["workstreams"] = snapshot.Workstreams.Count,
            ["sessions"] = snapshot.Sessions.Count,
            ["overwroteExisting"] = conflicts.Count
        }, NextCommandHintContext.General("List imported cells, then show the cell you want to continue."));
        return 0;
    }

    private static async Task SaveSessionAttachment(
        ICellStore store,
        CellMissionRecord mission,
        CellWorkstreamRecord workstream,
        CellAgentSessionRecord session,
        DateTimeOffset now,
        string eventSummary)
    {
        await store.SaveAgentSessionAsync(session);
        await store.UpdateWorkstreamAsync(workstream.Id, current =>
        {
            var workstreamSessions = current.SessionIds.ToList();
            AddUnique(workstreamSessions, session.Id);
            return current with
            {
                SessionIds = workstreamSessions,
                Status = session.Status is "handoff" or "blocked"
                    ? session.Status
                    : current.Status == "planned"
                        ? session.Status
                        : current.Status,
                UpdatedAtUtc = now
            };
        });
        await store.UpdateMissionAsync(mission.Id, current =>
        {
            var missionSessions = current.SessionIds.ToList();
            AddUnique(missionSessions, session.Id);
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "sessionAttached",
                Summary = eventSummary
            });

            return current with
            {
                SessionIds = missionSessions,
                Events = events,
                Status = current.Status == "planned" ? "in-progress" : current.Status,
                UpdatedAtUtc = now
            };
        });
    }

    private static async Task SaveSessionRunUpdate(
        ICellStore store,
        CellMissionRecord mission,
        CellWorkstreamRecord workstream,
        CellAgentSessionRecord session,
        DateTimeOffset now,
        bool attachIfMissing)
    {
        if (attachIfMissing)
        {
            await SaveSessionAttachment(store, mission, workstream, session, now, "Session run recorded.");
            return;
        }

        await store.SaveAgentSessionAsync(session);
        await UpdateSessionParents(store, session, now, "sessionRunUpdated", "Session run updated.");
    }

    private static async Task<CellSessionRunOutcome> SaveSessionRunFailure(
        ICellStore store,
        CellMissionRecord mission,
        CellWorkstreamRecord workstream,
        CellAgentSessionRecord session,
        string summary,
        string? evidence)
    {
        var now = DateTimeOffset.UtcNow;
        var blocker = new CellBlockerRecord
        {
            AtUtc = now,
            Summary = summary,
            Evidence = evidence
        };
        var events = session.Events.ToList();
        events.Add(new CellEventRecord
        {
            AtUtc = now,
            Type = "sessionRunFailed",
            Summary = summary
        });

        var blocked = session with
        {
            Status = "blocked",
            UpdatedAtUtc = now,
            Events = events,
            Blocker = blocker
        };
        await SaveSessionRunUpdate(store, mission, workstream, blocked, now, attachIfMissing: false);
        return new CellSessionRunOutcome(blocked, blocker);
    }

    private static async Task UpdateSessionParents(
        ICellStore store,
        CellAgentSessionRecord session,
        DateTimeOffset now,
        string eventType,
        string summary)
    {
        if (!string.IsNullOrWhiteSpace(session.WorkstreamId))
        {
            try
            {
                await store.UpdateWorkstreamAsync(session.WorkstreamId, current =>
                {
                    var sessionIds = current.SessionIds.ToList();
                    AddUnique(sessionIds, session.Id);
                    return current with
                    {
                        SessionIds = sessionIds,
                        Status = session.Status is "handoff" or "blocked" ? session.Status : current.Status,
                        UpdatedAtUtc = now
                    };
                });
            }
            catch (ArgumentException)
            {
                // A session can outlive a manually removed workstream; keep the session update useful.
            }
        }

        if (!string.IsNullOrWhiteSpace(session.MissionId))
        {
            try
            {
                await store.UpdateMissionAsync(session.MissionId, current =>
                {
                    var sessionIds = current.SessionIds.ToList();
                    AddUnique(sessionIds, session.Id);
                    var events = current.Events.ToList();
                    events.Add(new CellEventRecord
                    {
                        AtUtc = now,
                        Type = eventType,
                        Summary = summary
                    });

                    return current with
                    {
                        SessionIds = sessionIds,
                        Events = events,
                        UpdatedAtUtc = now
                    };
                });
            }
            catch (ArgumentException)
            {
                // A linked/manual session record should still accept handoffs and blockers.
            }
        }
    }

    private static JsonObject ToLaunchJson(CellLaunchResult result) =>
        new()
        {
            ["cellID"] = result.MissionId,
            ["missionID"] = result.MissionId,
            ["backend"] = result.Backend,
            ["dryRun"] = result.DryRun,
            ["eligible"] = result.EligibleCount,
            ["launchedCount"] = result.LaunchedCount,
            ["skippedCount"] = result.SkippedCount,
            ["failureCount"] = result.FailureCount,
            ["launched"] = result.Launched,
            ["skipped"] = result.Skipped
        };

    private static JsonObject LaunchSkip(CellWorkstreamRecord workstream, string reason) =>
        new()
        {
            ["workstreamID"] = workstream.Id,
            ["name"] = workstream.Name,
            ["status"] = workstream.Status,
            ["reason"] = reason
        };

    private static IReadOnlyList<CellWorkstreamRecord> OrderWorkstreams(
        CellMissionRecord mission,
        IReadOnlyList<CellWorkstreamRecord> workstreams)
    {
        var missionOrder = mission.WorkstreamIds
            .Select((id, index) => new { id, index })
            .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);

        return workstreams
            .OrderBy(item => missionOrder.TryGetValue(item.Id, out var index) ? index : int.MaxValue)
            .ThenBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<CellAgentSessionRecord> SessionsForWorkstream(
        CellWorkstreamRecord workstream,
        IReadOnlyList<CellAgentSessionRecord> sessions)
    {
        return sessions
            .Where(session =>
                string.Equals(session.WorkstreamId, workstream.Id, StringComparison.OrdinalIgnoreCase)
                || workstream.SessionIds.Contains(session.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static string BuildLaunchPrompt(
        CellMissionRecord mission,
        CellWorkstreamRecord workstream,
        IReadOnlyList<CellAgentSessionRecord> sessions,
        string extraPrompt)
    {
        var brief = BuildDelegationBrief(mission, workstream, sessions);
        if (string.IsNullOrWhiteSpace(extraPrompt))
        {
            return brief;
        }

        return brief.TrimEnd() + Environment.NewLine + Environment.NewLine
               + "## Coordinator Agent Prompt" + Environment.NewLine + Environment.NewLine
               + extraPrompt.Trim() + Environment.NewLine;
    }

    private static ImmutableDictionary<string, string> BuildCopilotOptions(CellArgs options)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.CopilotAllowTools.Count > 0)
        {
            builder["copilot.allowTool"] = string.Join(';', options.CopilotAllowTools);
        }

        if (options.CopilotAllowUrls.Count > 0)
        {
            builder["copilot.allowUrl"] = string.Join(';', options.CopilotAllowUrls);
        }

        if (options.CopilotAllowAll)
        {
            builder["copilot.allowAll"] = "true";
        }

        return builder.ToImmutable();
    }

    private static async Task<CellAgentSessionRecord> MarkCellSessionSyncFailed(
        ICellStore store,
        CellAgentSessionRecord session,
        string error)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await store.UpdateAgentSessionAsync(session.Id, current =>
        {
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "syncFailed",
                Summary = error
            });

            return current with
            {
                Status = "sync-failed",
                Events = events,
                Blocker = new CellBlockerRecord
                {
                    AtUtc = now,
                    Summary = "Session sync failed.",
                    Evidence = error
                },
                UpdatedAtUtc = now
            };
        });

        await UpdateSessionParents(store, updated, now, "sessionSyncFailed", error);
        return updated;
    }

    private static async Task<CellAgentSessionRecord> SkipExternalSessionSync(
        ICellStore store,
        CellAgentSessionRecord session,
        DateTimeOffset now)
    {
        return await store.UpdateAgentSessionAsync(session.Id, current =>
        {
            var events = current.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "syncSkipped",
                Summary = $"Skipped sync for external backend '{current.Backend}'."
            });

            return current with
            {
                Events = events,
                UpdatedAtUtc = now
            };
        });
    }

    private static CellSupervisionCounts CountSupervisionStatuses(IReadOnlyList<CellAgentSessionRecord> sessions)
    {
        var quiet = 0;
        var active = 0;
        var blocked = 0;
        var handoff = 0;

        foreach (var session in sessions)
        {
            switch (ClassifySupervisionStatus(session))
            {
                case "active":
                    active++;
                    break;
                case "blocked":
                    blocked++;
                    break;
                case "handoff":
                    handoff++;
                    break;
                default:
                    quiet++;
                    break;
            }
        }

        return new CellSupervisionCounts(quiet, active, blocked, handoff);
    }

    private static JsonArray BuildSupervisionSessionsJson(IReadOnlyList<CellAgentSessionRecord> sessions)
    {
        var array = new JsonArray();
        foreach (var session in sessions.OrderBy(item => item.WorkstreamId, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            array.Add(new JsonObject
            {
                ["sessionID"] = session.Id,
                ["workstreamID"] = session.WorkstreamId,
                ["backend"] = session.Backend,
                ["status"] = session.Status,
                ["category"] = ClassifySupervisionStatus(session),
                ["updatedAtUtc"] = session.UpdatedAtUtc.ToString("O")
            });
        }

        return array;
    }

    private static string ClassifySupervisionStatus(CellAgentSessionRecord session)
    {
        if (IsArchivedStatus(session.Status))
        {
            return "archived";
        }

        if (session.FinalHandoff is not null || IsCompleteStatus(session.Status))
        {
            return "handoff";
        }

        if (IsBlockedStatus(session.Status))
        {
            return "blocked";
        }

        var latestObservation = session.StatusObservations.LastOrDefault();
        if (latestObservation is not null)
        {
            if (IsBlockedStatus(latestObservation.EffectiveStatus)) return "blocked";
            if (IsActiveStatus(latestObservation.EffectiveStatus)) return "active";
        }

        return IsActiveStatus(session.Status) ? "active" : "quiet";
    }

    private static bool IsCompleteStatus(string? status) =>
        status is not null
        && (status.Equals("handoff", StringComparison.OrdinalIgnoreCase)
            || status.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("done", StringComparison.OrdinalIgnoreCase)
            || status.Equals("verified", StringComparison.OrdinalIgnoreCase)
            || status.Equals("closed", StringComparison.OrdinalIgnoreCase));

    private static bool IsArchivedStatus(string? status) =>
        status is not null
        && (status.Equals("archived", StringComparison.OrdinalIgnoreCase)
            || status.Equals("archive", StringComparison.OrdinalIgnoreCase));

    private static bool IsBlockedStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && (status.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || status.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("error", StringComparison.OrdinalIgnoreCase));

    private static bool IsActiveStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && (status.Contains("running", StringComparison.OrdinalIgnoreCase)
            || status.Contains("queued", StringComparison.OrdinalIgnoreCase)
            || status.Contains("waiting", StringComparison.OrdinalIgnoreCase)
            || status.Contains("active", StringComparison.OrdinalIgnoreCase)
            || status.Contains("busy", StringComparison.OrdinalIgnoreCase)
            || status.Contains("working", StringComparison.OrdinalIgnoreCase)
            || status.Contains("in-progress", StringComparison.OrdinalIgnoreCase)
            || status.Contains("pending", StringComparison.OrdinalIgnoreCase));

    private static int CountJsonFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Count()
            : 0;

    private static long SumFileBytes(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    private static async Task<CellStoreSnapshot> ReadCellSnapshot(CellArgs options)
    {
        var raw = !string.IsNullOrWhiteSpace(options.File)
            ? await File.ReadAllTextAsync(options.File)
            : Console.IsInputRedirected
                ? await Console.In.ReadToEndAsync()
                : throw new ArgumentException("Snapshot input is required. Use --file or pipe JSON to stdin.");

        var snapshot = JsonSerializer.Deserialize<CellStoreSnapshot>(raw, JsonOptions)
                       ?? throw new ArgumentException("Snapshot JSON did not contain an object.");
        snapshot = snapshot with
        {
            Missions = snapshot.Missions ?? [],
            Workstreams = snapshot.Workstreams ?? [],
            Sessions = snapshot.Sessions ?? []
        };

        if (snapshot.Kind is not ("cellStoreSnapshot" or "workMapSnapshot"))
        {
            throw new ArgumentException("Snapshot kind must be 'cellStoreSnapshot' or legacy 'workMapSnapshot'.");
        }

        foreach (var mission in snapshot.Missions)
        {
            ValidateRecordId(mission.Id, "cell");
        }

        foreach (var workstream in snapshot.Workstreams)
        {
            ValidateRecordId(workstream.Id, "workstream");
        }

        foreach (var session in snapshot.Sessions)
        {
            ValidateRecordId(session.Id, "session");
        }

        return snapshot;
    }

    private static async Task<JsonArray> FindSnapshotConflicts(ICellStore store, CellStoreSnapshot snapshot)
    {
        var conflicts = new JsonArray();
        foreach (var mission in snapshot.Missions)
        {
            if (await store.GetMissionAsync(mission.Id) is not null)
            {
                conflicts.Add(new JsonObject { ["kind"] = "cell", ["legacyKind"] = "mission", ["id"] = mission.Id });
            }
        }

        foreach (var workstream in snapshot.Workstreams)
        {
            if (await store.GetWorkstreamAsync(workstream.Id) is not null)
            {
                conflicts.Add(new JsonObject { ["kind"] = "workstream", ["id"] = workstream.Id });
            }
        }

        foreach (var session in snapshot.Sessions)
        {
            if (await store.GetAgentSessionAsync(session.Id) is not null)
            {
                conflicts.Add(new JsonObject { ["kind"] = "session", ["id"] = session.Id });
            }
        }

        return conflicts;
    }

    private static void ValidateRecordId(string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException($"Snapshot contains a {kind} record without an id.");
        }
    }

    private static async Task LinkChildCell(ICellStore store, string parentCellId, string childCellId, DateTimeOffset now)
    {
        await store.UpdateMissionAsync(parentCellId, parent =>
        {
            var childCellIds = parent.ChildCellIds.ToList();
            AddUnique(childCellIds, childCellId);

            var events = parent.Events.ToList();
            events.Add(new CellEventRecord
            {
                AtUtc = now,
                Type = "childCellLinked",
                Summary = $"Child cell {childCellId} linked."
            });

            var edges = parent.Edges.ToList();
            if (!edges.Any(edge =>
                    string.Equals(edge.FromId, parentCellId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(edge.ToId, childCellId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(edge.Kind, "contains", StringComparison.OrdinalIgnoreCase)))
            {
                edges.Add(new CellEdgeRecord
                {
                    FromId = parentCellId,
                    ToId = childCellId,
                    Kind = "contains",
                    Summary = "Parent cell contains child cell."
                });
            }

            return parent with
            {
                ChildCellIds = childCellIds,
                Events = events,
                Edges = edges,
                UpdatedAtUtc = now
            };
        });
    }

    private static async Task<CellMissionRecord> RequireMission(ICellStore store, string? missionId)
    {
        var id = Require(missionId, "--cell");
        var mission = await store.GetMissionAsync(id);
        return mission ?? throw new ArgumentException($"Unknown cell '{id}'.");
    }

    private static async Task<CellWorkstreamRecord> RequireWorkstream(ICellStore store, string? streamId)
    {
        var id = Require(streamId, "--stream");
        var workstream = await store.GetWorkstreamAsync(id);
        return workstream ?? throw new ArgumentException($"Unknown cell stream '{id}'.");
    }

    private static async Task<CellAgentSessionRecord> RequireAgentSession(ICellStore store, string? sessionId)
    {
        var id = Require(sessionId, "--session");
        var session = await store.GetAgentSessionAsync(id);
        return session ?? throw new ArgumentException($"Unknown cell agent session '{id}'.");
    }

    private static void EnsureMissionOwnsWorkstream(CellMissionRecord mission, CellWorkstreamRecord workstream)
    {
        if (!string.Equals(workstream.MissionId, mission.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Workstream '{workstream.Id}' belongs to cell '{workstream.MissionId}', not '{mission.Id}'.");
        }
    }

    private static void EnsureMissionOwnsSession(CellMissionRecord mission, CellAgentSessionRecord session)
    {
        if (!string.Equals(session.MissionId, mission.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Session '{session.Id}' belongs to cell '{session.MissionId}', not '{mission.Id}'.");
        }
    }

    private static ResolvedAgentProfile ResolveCellProfile(CellArgs options)
    {
        BackendKind? backend = null;
        if (!string.IsNullOrWhiteSpace(options.Backend))
        {
            backend = ParseBackend(options.Backend);
        }

        var defaultBackend = options.ShouldAutoSelectBackend
            ? BackendAvailabilityDetector.PreferredAvailableBackend() ?? BackendKind.Codex
            : BackendKind.Codex;
        var resolved = new AgentProfileResolver(DefaultAgentConfiguration with { DefaultBackend = defaultBackend }).Resolve(
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

        return ResolveCellBackendCompatibility(resolved);
    }

    private static ResolvedAgentProfile ResolveCellBackendCompatibility(ResolvedAgentProfile resolved)
    {
        return resolved.Backend == BackendKind.Copilot
               && string.Equals(resolved.ModelProvider, "github-copilot", StringComparison.OrdinalIgnoreCase)
            ? resolved with { Backend = BackendKind.Opencode }
            : resolved;
    }

    private static BackendKind ParseBackend(string value)
    {
        if (!BackendKindExtensions.TryParse(value, out var backend))
        {
            throw new ArgumentException($"Unsupported backend '{value}'. Use opencode, codex, pi, or copilot.");
        }

        return backend;
    }

    private static string? ValidateCellSessionRun(ResolvedAgentProfile resolved, CellArgs options)
    {
        return options.Async && !BackendAvailabilityDetector.SupportsDetachedAsync(resolved.Backend)
            ? $"{resolved.Backend.ToOptionValue()} backend does not support --async yet; run without --async/--wait for a blocking one-shot prompt."
            : null;
    }

    private static string NormalizeLinkedBackend(string value)
    {
        if (BackendKindExtensions.TryParse(value, out var backend))
        {
            return backend.ToOptionValue();
        }

        var normalized = value.Trim().ToLowerInvariant().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Session backend cannot be empty.");
        }

        if (normalized.Length > 64)
        {
            throw new ArgumentException("Session backend labels must be 64 characters or fewer.");
        }

        foreach (var ch in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '.')
            {
                throw new ArgumentException($"Unsupported session backend label '{value}'. Use a known backend or a lower-kebab external label such as manual, external, or shipper.");
            }
        }

        return normalized;
    }

    private static async Task<string> ReadCellPrompt(CellArgs options)
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

    private static string BuildCellMarkdown(CellBundle bundle)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {bundle.Mission.Title}");
        builder.AppendLine();
        builder.AppendLine($"- Cell: `{bundle.Mission.Id}`");
        builder.AppendLine($"- Status: {bundle.Mission.Status}");
        if (!string.IsNullOrWhiteSpace(bundle.Mission.ParentCellId)) builder.AppendLine($"- Parent cell: `{bundle.Mission.ParentCellId}`");
        if (bundle.Mission.ChildCellIds.Count > 0) builder.AppendLine($"- Child cells: {string.Join(", ", bundle.Mission.ChildCellIds.Select(id => $"`{id}`"))}");
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
        builder.AppendLine("| Session | Display | Backend | Model | Agent | Status | Messages | Observations | Verification | Handoff |");
        builder.AppendLine("|---------|---------|---------|-------|-------|--------|----------|--------------|--------------|---------|");
        foreach (var session in bundle.Sessions)
        {
            var handoff = session.FinalHandoff?.Text is null ? "" : FirstLine(session.FinalHandoff.Text);
            builder.AppendLine(
                $"| `{session.Id}` | {EscapePipe(session.DisplayName)} | {EscapePipe(session.Backend)} | {EscapePipe(session.Model)} | {EscapePipe(session.Agent)} | {EscapePipe(session.Status)} | {session.Messages.Count} | {session.StatusObservations.Count} | {session.Verification.Count} | {EscapePipe(handoff)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Cell Evidence");
        builder.AppendLine();
        foreach (var evidence in bundle.Mission.Evidence)
        {
            builder.AppendLine($"- `{evidence.Id}` {evidence.Kind}: {evidence.Summary}");
        }

        builder.AppendLine();
        builder.AppendLine("## Timeline");
        builder.AppendLine();
        foreach (var item in bundle.Mission.Events.OrderBy(item => item.AtUtc))
        {
            builder.AppendLine($"- {item.AtUtc:u} `{item.Type}` {item.Summary}");
        }

        return builder.ToString();
    }

    private static string BuildCellHtml(CellBundle bundle)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine($"<title>{Html(bundle.Mission.Title)} - Aegis Cell</title>");
        builder.AppendLine("<style>body{font-family:system-ui,sans-serif;margin:32px;line-height:1.4;color:#17202a;background:#f7f8fb}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:16px}.card{background:white;border:1px solid #d8dee9;border-radius:8px;padding:16px}.meta{color:#596579;font-size:13px}.status{font-weight:700}.pill{display:inline-block;border:1px solid #cbd5e1;border-radius:999px;padding:2px 8px;margin:2px;font-size:12px;background:#f8fafc}.timeline{border-left:2px solid #d8dee9;padding-left:14px}.event{margin:0 0 10px}.excerpt{white-space:pre-wrap;max-height:11em;overflow:auto;background:#f8fafc;border:1px solid #e5e7eb;border-radius:6px;padding:8px;font-size:13px}</style></head><body>");
        builder.AppendLine($"<h1>{Html(bundle.Mission.Title)}</h1>");
        builder.AppendLine($"<p class=\"meta\">Cell {Html(bundle.Mission.Id)} · {Html(bundle.Mission.Status)}</p>");
        if (!string.IsNullOrWhiteSpace(bundle.Mission.ParentCellId)) builder.AppendLine($"<p class=\"meta\">Parent cell {Html(bundle.Mission.ParentCellId)}</p>");
        if (bundle.Mission.ChildCellIds.Count > 0) builder.AppendLine($"<p class=\"meta\">Child cells {Html(string.Join(", ", bundle.Mission.ChildCellIds))}</p>");
        if (!string.IsNullOrWhiteSpace(bundle.Mission.Intent)) builder.AppendLine($"<p>{Html(bundle.Mission.Intent)}</p>");
        if (!string.IsNullOrWhiteSpace(bundle.Mission.NextAction)) builder.AppendLine($"<p><strong>Next:</strong> {Html(bundle.Mission.NextAction)}</p>");
        builder.AppendLine("<h2>Agent Sessions</h2><div class=\"grid\">");
        foreach (var session in bundle.Sessions)
        {
            builder.AppendLine("<section class=\"card\">");
            builder.AppendLine($"<h3>{Html(session.DisplayName ?? session.Id)}</h3>");
            builder.AppendLine($"<p class=\"meta\">{Html(session.Backend)} {Html(session.Provider)} {Html(session.Model)} {Html(session.Agent)} · {Html(session.WorkstreamId)}</p>");
            builder.AppendLine($"<p class=\"status\">{Html(session.Status)}</p>");
            if (session.FinalHandoff is not null) builder.AppendLine($"<p>{Html(FirstLine(session.FinalHandoff.Text))}</p>");
            if (session.Blocker is not null) builder.AppendLine($"<p><strong>Blocker:</strong> {Html(session.Blocker.Summary)}</p>");
            builder.AppendLine($"<p class=\"meta\">Evidence: {session.Evidence.Count} · Events: {session.Events.Count} · Messages: {session.Messages.Count} · Checks: {session.Verification.Count}</p>");
            if (session.StatusObservations.Count > 0)
            {
                var latest = session.StatusObservations.OrderBy(item => item.AtUtc).Last();
                builder.AppendLine($"<p><span class=\"pill\">{Html(latest.EffectiveStatus)}</span><span class=\"pill\">messages {latest.MessageCount}</span></p>");
            }

            foreach (var verification in session.Verification.TakeLast(3))
            {
                builder.AppendLine($"<p class=\"meta\">{Html(verification.Kind)}: {Html(verification.Result)} {Html(verification.Summary)}</p>");
            }

            foreach (var message in session.Messages.TakeLast(2))
            {
                builder.AppendLine($"<div class=\"excerpt\"><strong>{Html(message.Role)}</strong>: {Html(message.Text)}</div>");
            }

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

        builder.AppendLine("</div><h2>Cell Evidence</h2><div class=\"grid\">");
        foreach (var evidence in bundle.Mission.Evidence)
        {
            builder.AppendLine("<section class=\"card\">");
            builder.AppendLine($"<h3>{Html(evidence.Kind)}</h3>");
            builder.AppendLine($"<p class=\"meta\">{Html(evidence.Id)} · {evidence.AddedAtUtc:u}</p>");
            if (!string.IsNullOrWhiteSpace(evidence.Path)) builder.AppendLine($"<p><code>{Html(evidence.Path)}</code></p>");
            if (!string.IsNullOrWhiteSpace(evidence.Summary)) builder.AppendLine($"<p>{Html(evidence.Summary)}</p>");
            builder.AppendLine("</section>");
        }

        builder.AppendLine("</div><h2>Status Timeline</h2><div class=\"card timeline\">");
        foreach (var item in bundle.Mission.Events.OrderBy(item => item.AtUtc))
        {
            builder.AppendLine($"<p class=\"event\"><span class=\"meta\">{item.AtUtc:u}</span><br><strong>{Html(item.Type)}</strong> {Html(item.Summary)}</p>");
        }

        builder.AppendLine("</div>");
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static string BuildDelegationBrief(
        CellMissionRecord mission,
        CellWorkstreamRecord workstream,
        IReadOnlyList<CellAgentSessionRecord> sessions)
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
        builder.AppendLine("## Coordinator Agent Context");
        builder.AppendLine();
        builder.AppendLine($"- Cell: `{mission.Id}` - {mission.Title}");
        if (!string.IsNullOrWhiteSpace(mission.ParentCellId)) builder.AppendLine($"- Parent cell: `{mission.ParentCellId}`");
        if (mission.ChildCellIds.Count > 0) builder.AppendLine($"- Child cells: {string.Join(", ", mission.ChildCellIds)}");
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
        builder.AppendLine("- Keep changes and conclusions scoped to this workstream unless the coordinator agent asks for expansion.");
        builder.AppendLine();
        builder.AppendLine("## Expected Output");
        builder.AppendLine();
        builder.AppendLine("- A concise final handoff with outcome, files changed or facts found, commands run, evidence, and residual risk.");
        builder.AppendLine();
        builder.AppendLine("## Evidence To Return");
        builder.AppendLine();
        builder.AppendLine("- Diffs, logs, test output, source paths, session exports, screenshots, or exact blocker evidence that the coordinator agent can inspect.");
        builder.AppendLine();
        builder.AppendLine("## Stop Or Report A Blocker When");
        builder.AppendLine();
        builder.AppendLine("- The assigned clone/session is unavailable, instructions conflict, a required permission or secret is missing, or continuing would risk corrupting unrelated work.");
        return builder.ToString();
    }

    private static List<CellMessageRecord> ToCellMessages(IReadOnlyList<BackendMessage> messages)
    {
        var records = new List<CellMessageRecord>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            records.Add(new CellMessageRecord
            {
                Id = string.IsNullOrWhiteSpace(message.Id) ? $"message-{index:D6}" : message.Id,
                Role = message.Role,
                Text = Truncate(message.Text, 4_000),
                PartId = message.PartId,
                Timestamp = message.Timestamp,
                Sequence = index,
                IsExcerpt = message.Text.Length > 4_000
            });
        }

        return records;
    }

    private static IReadOnlyList<BackendMessage> LimitBackendMessages(IReadOnlyList<BackendMessage> messages, int limit)
    {
        if (limit <= 0 || messages.Count <= limit)
        {
            return messages;
        }

        return messages.Skip(messages.Count - limit).ToArray();
    }

    private static int LatestUserMessageIndex(IReadOnlyList<BackendMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (string.Equals(messages[index].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ShouldMarkNeedsRestartOrNudge(
        CellAgentSessionRecord session,
        SessionStateSnapshot? state,
        DateTimeOffset now) =>
        state is not null
        && !IsActiveStatus(state.EffectiveStatus)
        && HasSessionWaitExpired(session, now);

    private static string BuildMissingHandoffRecoverySummary(
        SessionStateSnapshot? state,
        IReadOnlyList<BackendMessage> messages,
        int anchorMessageIndex,
        string marker)
    {
        var assistantAfterPrompt = messages
            .Skip(Math.Max(anchorMessageIndex + 1, 0))
            .Where(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var assistantWithText = assistantAfterPrompt.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message.Text));
        var status = state?.EffectiveStatus ?? "unknown";

        return assistantAfterPrompt.Length == 0
            ? $"Session is {NeedsRestartOrNudgeStatus}: observed status '{status}' after sync with no assistant message after the latest user prompt."
            : assistantWithText is null
                ? $"Session is {NeedsRestartOrNudgeStatus}: observed status '{status}' after sync with {assistantAfterPrompt.Length} assistant message(s), but their text was empty."
                : $"Session is {NeedsRestartOrNudgeStatus}: observed status '{status}' after sync with assistant output after the latest user prompt, but no '{marker}' marker.";
    }

    private static bool HasSessionWaitExpired(CellAgentSessionRecord session, DateTimeOffset now)
    {
        if (!string.Equals(session.Status, "queued", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var timeoutSeconds = session.TimeoutSeconds.GetValueOrDefault(300);
        return now - session.CreatedAtUtc >= TimeSpan.FromSeconds(timeoutSeconds);
    }

    private static bool ShouldKeepAsyncSessionQueued(
        CellAgentSessionRecord session,
        SessionStateSnapshot? state,
        DateTimeOffset now) =>
        string.Equals(session.Status, "queued", StringComparison.OrdinalIgnoreCase)
        && state is not null
        && !IsActiveStatus(state.EffectiveStatus)
        && !HasSessionWaitExpired(session, now);

    private static List<CellMessageRecord> MergeCellMessages(
        IReadOnlyList<CellMessageRecord> existing,
        IReadOnlyList<CellMessageRecord> incoming)
    {
        var merged = existing.ToList();
        var seen = new HashSet<string>(merged.Select(MessageKey), StringComparer.Ordinal);
        foreach (var message in incoming)
        {
            if (seen.Add(MessageKey(message)))
            {
                merged.Add(message with { Sequence = merged.Count });
            }
        }

        return merged;
    }

    private static List<CellMessageRecord> EnsurePromptMessage(
        string prompt,
        DateTimeOffset atUtc,
        List<CellMessageRecord> messages)
    {
        if (messages.Any(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)))
        {
            return messages;
        }

        var result = new List<CellMessageRecord>
        {
            new()
            {
                Id = $"prompt-{atUtc.ToUnixTimeMilliseconds()}",
                Role = "user",
                Text = Truncate(prompt, 4_000),
                Timestamp = atUtc,
                Sequence = 0,
                IsExcerpt = prompt.Length > 4_000
            }
        };
        result.AddRange(messages.Select((message, index) => message with { Sequence = index + 1 }));
        return result;
    }

    private static CellStatusObservationRecord ToCellStatusObservation(SessionStateSnapshot state, DateTimeOffset atUtc) =>
        new()
        {
            AtUtc = atUtc,
            ApiStatus = state.ApiStatus,
            EffectiveStatus = state.EffectiveStatus,
            DerivedStatus = state.DerivedStatus,
            MessageCount = state.MessageCount,
            LatestUserMessageId = state.LatestUserMessageId,
            LatestAssistantMessageId = state.LatestAssistantMessageId,
            HasFreshSummary = state.HasFreshSummary
        };

    private static string MessageKey(CellMessageRecord message) =>
        $"{message.Id}\u001f{message.PartId}\u001f{message.Role}";

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "\n[truncated]";

    private static string WithInlineHints(string text, NextCommandHintContext context, CellArgs options)
    {
        return string.IsNullOrWhiteSpace(options.Output)
            ? AppendNextCommandHints(text, context)
            : text;
    }

    private static string AppendNextCommandHints(string text, NextCommandHintContext context)
    {
        var hints = RenderNextCommandHints(context);
        return text.EndsWith(Environment.NewLine, StringComparison.Ordinal)
            ? text + hints
            : text + Environment.NewLine + hints;
    }

    private static void WriteCellJson(JsonNode? node, NextCommandHintContext context)
    {
        WriteJson(node);
        WriteCellNextAction(context);
    }

    private static void WriteCellNextAction(NextCommandHintContext context)
    {
        Console.Error.WriteLine(BuildCellNextActionJson(context).ToJsonString(CellNextActionJsonOptions));
        Console.Error.Flush();
    }

    private static string[] BuildSessionRunNextCommands(string missionId, CellAgentSessionRecord session) =>
    [
        $"aegis cell supervise --cell {missionId} --until-idle --max-runs 1",
        $"aegis last-summary --backend {session.Backend} --session {session.Id} --plain"
    ];

    private static JsonObject BuildCellNextActionJson(NextCommandHintContext context) =>
        new()
        {
            ["kind"] = "cell-next-action",
            ["suggestedNextAction"] = SuggestedNextAction(context),
            ["cellID"] = string.IsNullOrWhiteSpace(context.MissionId) ? null : context.MissionId,
            ["missionID"] = string.IsNullOrWhiteSpace(context.MissionId) ? null : context.MissionId,
            ["streamID"] = string.IsNullOrWhiteSpace(context.StreamId) ? null : context.StreamId,
            ["sessionID"] = string.IsNullOrWhiteSpace(context.SessionId) ? null : context.SessionId,
            ["backend"] = string.IsNullOrWhiteSpace(context.Backend) ? null : context.Backend,
            ["nextCommands"] = JsonSerializer.SerializeToNode(BuildNextCommands(context), JsonOptions),
            ["notes"] = JsonSerializer.SerializeToNode(CellNextActionNotes, JsonOptions)
        };

    private static readonly string[] CellNextActionNotes =
    [
        "Use --backend copilot without a github-copilot provider model only for the standalone Copilot CLI backend.",
        "Use --model github-copilot/gpt-5.5 with cell session run or aegis ask for OpenCode sessions using the GitHub Copilot provider.",
        "The legacy `aegis cell` and `aegis cell` command forms remain accepted during migration; prefer `aegis cell` in new briefs and docs."
    ];

    private static readonly JsonSerializerOptions CellNextActionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static string SuggestedNextAction(NextCommandHintContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.SuggestedNextAction))
        {
            return context.SuggestedNextAction;
        }

        if (!string.IsNullOrWhiteSpace(context.SessionId))
        {
            return "Sync or inspect the linked session, then update the stream status.";
        }

        if (!string.IsNullOrWhiteSpace(context.StreamId))
        {
            return "Launch a linked worker for this stream, or supervise the cell if a worker is already attached.";
        }

        if (!string.IsNullOrWhiteSpace(context.MissionId))
        {
            return "Inspect the cell, add a stream or child cell if needed, then launch linked workers.";
        }

        return "List existing cells or create a cell before launching linked workers.";
    }

    private static string[] BuildNextCommands(NextCommandHintContext context)
    {
        var mission = string.IsNullOrWhiteSpace(context.MissionId) ? "<cell>" : context.MissionId;
        var stream = string.IsNullOrWhiteSpace(context.StreamId) ? "<stream>" : context.StreamId;
        var directory = QuoteExampleValue(string.IsNullOrWhiteSpace(context.Directory) ? "<dir>" : context.Directory);
        var role = QuoteExampleValue(string.IsNullOrWhiteSpace(context.Role) ? "<role>" : context.Role);
        var commands = new List<string>();

        if (!string.IsNullOrWhiteSpace(context.SessionId))
        {
            commands.Add($"aegis cell session sync --session {context.SessionId}");
            commands.Add($"aegis cell show --cell {mission} --format md");
            if (!string.IsNullOrWhiteSpace(context.Backend) && BackendKindExtensions.TryParse(context.Backend, out _))
            {
                commands.Add($"aegis last-summary --backend {context.Backend} --session {context.SessionId} --plain");
            }
            else
            {
                commands.Add($"aegis cell session handoff --session {context.SessionId} --summary \"<handoff>\"");
                commands.Add($"aegis cell session blocker set --session {context.SessionId} --summary \"<blocker>\"");
            }

            return commands.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(context.StreamId))
        {
            commands.Add($"aegis cell session run --cell {mission} --stream {stream} --model github-copilot/gpt-5.5 --variant high --agent build --directory {directory} --prompt-file \"<brief.md>\" --timeout 900 --async");
            commands.Add($"aegis ask --model github-copilot/gpt-5.5 --variant high --agent build --directory {directory} --prompt-file \"<brief.md>\" --timeout 900");
            commands.Add($"aegis cell session link --cell {mission} --stream {stream} --session <ses_...> --backend opencode --role {role}");
            commands.Add($"aegis cell supervise --cell {mission} --until-idle --max-runs 1");
            return commands.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(context.MissionId))
        {
            commands.Add($"aegis cell show --cell {mission} --format md");
            commands.Add($"aegis cell stream add --cell {mission} --name \"<stream>\" --role \"<role>\" --clone \"<dir>\"");
            commands.Add($"aegis cell fork --cell {mission} --title \"<child cell>\" --intent \"<goal>\"");
            commands.Add($"aegis cell session run --cell {mission} --stream <stream> --model github-copilot/gpt-5.5 --variant high --agent build --directory \"<dir>\" --prompt-file \"<brief.md>\" --timeout 900 --async");
            commands.Add("aegis ask --model github-copilot/gpt-5.5 --variant high --agent build --directory \"<dir>\" --prompt-file \"<brief.md>\" --timeout 900");
            return commands.ToArray();
        }

        commands.Add("aegis cell list --format md");
        commands.Add("aegis cell create --title \"<cell>\" --intent \"<goal>\"");
        commands.Add("aegis cell store info");
        return commands.ToArray();
    }

    private static string RenderNextCommandHints(NextCommandHintContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("Next useful commands:");
        builder.AppendLine();
        builder.AppendLine(SuggestedNextAction(context));
        foreach (var command in BuildNextCommands(context))
        {
            builder.AppendLine(command);
        }

        builder.AppendLine();
        foreach (var note in CellNextActionNotes)
        {
            builder.AppendLine(note);
        }

        return builder.ToString();
    }

    private static string QuoteExampleValue(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static async Task<string> ReadTextOption(CellArgs options, string required)
    {
        if (!string.IsNullOrWhiteSpace(options.Summary)) return options.Summary;
        if (!string.IsNullOrWhiteSpace(options.File))
        {
            if (!File.Exists(options.File)) throw new ArgumentException($"--file not found: {options.File}");
            var text = await File.ReadAllTextAsync(options.File);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return Require(null, required);
    }

    private static async Task WriteCellOutput(string text, CellArgs options)
    {
        if (string.IsNullOrWhiteSpace(options.Output))
        {
            Console.Write(text);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? Environment.CurrentDirectory);
        await File.WriteAllTextAsync(options.Output, text, Encoding.UTF8);
    }

    private static string NewCellId(string prefix) => $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..(prefix.Length + 24)];

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

    private sealed record CellBundle(
        CellMissionRecord Cell,
        IReadOnlyList<CellWorkstreamRecord> Workstreams,
        IReadOnlyList<CellAgentSessionRecord> Sessions)
    {
        public CellMissionRecord Mission => Cell;
    }

    private sealed record CellSessionRunOutcome(
        CellAgentSessionRecord Session,
        CellBlockerRecord? Blocker);

    private sealed record CellLaunchResult(
        string MissionId,
        string Backend,
        bool DryRun,
        int EligibleCount,
        int LaunchedCount,
        int SkippedCount,
        int FailureCount,
        JsonArray Launched,
        JsonArray Skipped);

    private sealed record CellSupervisionCounts(
        int Quiet,
        int Active,
        int Blocked,
        int Handoff);

    private sealed record NextCommandHintContext(
        string? MissionId,
        string? StreamId,
        string? SessionId,
        string? Backend,
        string? Directory,
        string? Role,
        string? SuggestedNextAction)
    {
        public static NextCommandHintContext General(string suggestedNextAction) =>
            new(null, null, null, null, null, null, suggestedNextAction);

        public static NextCommandHintContext ForMission(string missionId, string? suggestedNextAction = null) =>
            new(missionId, null, null, null, null, null, suggestedNextAction);

        public static NextCommandHintContext ForWorkstream(CellWorkstreamRecord workstream, string? suggestedNextAction = null) =>
            new(workstream.MissionId, workstream.Id, null, null, workstream.ClonePath, workstream.Role, suggestedNextAction);

        public static NextCommandHintContext ForSession(CellAgentSessionRecord session, string? suggestedNextAction = null) =>
            new(session.MissionId, session.WorkstreamId, session.Id, session.Backend, session.Directory, session.Role, suggestedNextAction);
    }

    private sealed class CellArgs
    {
        public List<string> Positionals { get; } = [];

        public string? MissionId { get; private set; }

        public string? ParentCellId { get; private set; }

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

        public bool BackendWasProvided { get; private set; }

        public string? BackendSessionId { get; private set; }

        public string? Provider { get; private set; }

        public string? Model { get; private set; }

        public string? Variant { get; private set; }

        public string? Agent { get; private set; }

        public string? System { get; private set; }

        public string? Directory { get; private set; }

        public string? Server { get; private set; }

        public string? Host { get; private set; }

        public string? Profile { get; private set; }

        public string? Prompt { get; private set; }

        public string? PromptFile { get; private set; }

        public string Format { get; private set; } = "json";

        public string? Output { get; private set; }

        public string? Status { get; private set; }

        public string? NextAction { get; private set; }

        public string? Kind { get; private set; }

        public string? Path { get; private set; }

        public string? AccessLogPath { get; private set; }

        public string? Summary { get; private set; }

        public string? File { get; private set; }

        public string? EvidenceText { get; private set; }

        public string? EvidenceId { get; private set; }

        public string? Result { get; private set; }

        public List<string> DependsOn { get; } = [];

        public List<string> CopilotAllowTools { get; } = [];

        public List<string> CopilotAllowUrls { get; } = [];

        public bool Async { get; private set; }

        public bool Wait { get; private set; }

        public bool Raw { get; private set; }

        public bool NoReply { get; private set; }

        public bool All { get; private set; }

        public bool Force { get; private set; }

        public bool DryRun { get; private set; }

        public bool IncludeComplete { get; private set; }

        public bool UntilIdle { get; private set; }

        public bool LaunchMissing { get; private set; }

        public bool CopilotAllowAll { get; private set; }

        public bool Help { get; private set; }

        public int TimeoutSeconds { get; private set; } = 300;

        public bool TimeoutWasProvided { get; private set; }

        public bool ShouldAutoSelectBackend =>
            !BackendWasProvided
            && string.IsNullOrWhiteSpace(Profile)
            && string.IsNullOrWhiteSpace(Model)
            && string.IsNullOrWhiteSpace(Variant)
            && string.IsNullOrWhiteSpace(Agent)
            && string.IsNullOrWhiteSpace(System);

        public int MessageLimit { get; private set; } = 50;

        public int? Port { get; private set; }

        public int IntervalSeconds { get; private set; } = 30;

        public int? MaxRuns { get; private set; }

        public int? MaxDurationMinutes { get; private set; }

        public string SummaryMarker { get; private set; } = "FINAL HANDOFF";

        public static CellArgs Parse(IEnumerable<string> args)
        {
            var parsed = new CellArgs();
            var queue = new Queue<string>(args);
            while (queue.Count > 0)
            {
                var arg = queue.Dequeue();
                switch (arg)
                {
                    case "--cell": parsed.MissionId = Value(queue, arg); break;
                    case "--mission": parsed.MissionId = Value(queue, arg); break;
                    case "--parent-cell": parsed.ParentCellId = Value(queue, arg); break;
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
                    case "--copilot-allow-tool": parsed.CopilotAllowTools.Add(Value(queue, arg)); break;
                    case "--copilot-allow-url": parsed.CopilotAllowUrls.Add(Value(queue, arg)); break;
                    case "--integration-action": parsed.IntegrationAction = Value(queue, arg); break;
                    case "--display-name": parsed.DisplayName = Value(queue, arg); break;
                    case "--backend": parsed.Backend = Value(queue, arg); parsed.BackendWasProvided = true; break;
                    case "--engine": parsed.Backend = Value(queue, arg); parsed.BackendWasProvided = true; break;
                    case "--backend-session": parsed.BackendSessionId = Value(queue, arg); break;
                    case "--provider": parsed.Provider = Value(queue, arg); break;
                    case "--model": parsed.Model = Value(queue, arg); break;
                    case "--variant": parsed.Variant = Value(queue, arg); break;
                    case "--reasoning": parsed.Variant = Value(queue, arg); break;
                    case "--agent": parsed.Agent = Value(queue, arg); break;
                    case "--system": parsed.System = Value(queue, arg); break;
                    case "--directory": parsed.Directory = Value(queue, arg); break;
                    case "--server": parsed.Server = Value(queue, arg); break;
                    case "--host": parsed.Host = Value(queue, arg); break;
                    case "--hostname": parsed.Host = Value(queue, arg); break;
                    case "--profile": parsed.Profile = Value(queue, arg); break;
                    case "--prompt": parsed.Prompt = Value(queue, arg); break;
                    case "--prompt-file": parsed.PromptFile = Value(queue, arg); break;
                    case "--format": parsed.Format = Value(queue, arg); break;
                    case "--output": parsed.Output = Value(queue, arg); break;
                    case "--status": parsed.Status = Value(queue, arg); break;
                    case "--next-action": parsed.NextAction = Value(queue, arg); break;
                    case "--kind": parsed.Kind = Value(queue, arg); break;
                    case "--path": parsed.Path = Value(queue, arg); break;
                    case "--access-log": parsed.AccessLogPath = Value(queue, arg); break;
                    case "--summary": parsed.Summary = Value(queue, arg); break;
                    case "--file": parsed.File = Value(queue, arg); break;
                    case "--input": parsed.File = Value(queue, arg); break;
                    case "--evidence": parsed.EvidenceText = Value(queue, arg); break;
                    case "--evidence-id": parsed.EvidenceId = Value(queue, arg); break;
                    case "--result": parsed.Result = Value(queue, arg); break;
                    case "--summary-marker": parsed.SummaryMarker = Value(queue, arg); break;
                    case "--async": parsed.Async = true; break;
                    case "--wait": parsed.Wait = true; break;
                    case "--raw": parsed.Raw = true; break;
                    case "--no-reply": parsed.NoReply = true; break;
                    case "--all": parsed.All = true; break;
                    case "--force": parsed.Force = true; break;
                    case "--dry-run": parsed.DryRun = true; break;
                    case "--include-complete": parsed.IncludeComplete = true; break;
                    case "--until-idle": parsed.UntilIdle = true; break;
                    case "--launch-missing": parsed.LaunchMissing = true; break;
                    case "--copilot-allow-all": parsed.CopilotAllowAll = true; break;
                    case "--help": parsed.Help = true; break;
                    case "-h": parsed.Help = true; break;
                    case "--interval-seconds":
                        parsed.IntervalSeconds = PositiveInt(Value(queue, arg), arg);
                        break;
                    case "--max-runs":
                        parsed.MaxRuns = PositiveInt(Value(queue, arg), arg);
                        break;
                    case "--max-duration-minutes":
                        parsed.MaxDurationMinutes = PositiveInt(Value(queue, arg), arg);
                        break;
                    case "--message-limit":
                        parsed.MessageLimit = PositiveInt(Value(queue, arg), arg);
                        break;
                    case "--port":
                        parsed.Port = PositiveInt(Value(queue, arg), arg);
                        break;
                    case "--timeout":
                        parsed.TimeoutSeconds = PositiveInt(Value(queue, arg), arg);
                        parsed.TimeoutWasProvided = true;
                        break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"Unknown cell option '{arg}'.");
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
