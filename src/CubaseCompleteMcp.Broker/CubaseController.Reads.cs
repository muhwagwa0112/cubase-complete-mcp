using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

public sealed partial class CubaseController
{
    private async Task<BrokerResponse> ReadAsync(BrokerRequest request, NormalizedOperation operation, string session,
        CancellationToken cancellationToken)
    {
        var args = operation.Arguments;
        switch (operation.Family)
        {
            case "status":
                return await StatusAsync(request, cancellationToken).ConfigureAwait(false);
            case "capabilities.search":
                return Capabilities(request, args);
            case "capabilities.list":
                return AllCapabilities(request);
            case "commands.list":
                return Commands(request);
            case "diagnostics":
                return operation.Action == "operation_status"
                    ? OperationStatus(request, args, session)
                    : await DiagnosticsAsync(request, cancellationToken).ConfigureAwait(false);
            case "command":
                var command = CommandLookup(args) ?? throw new PlanException(ErrorCodes.CommandNotFound, "Supply a valid command id or an exact category and name.");
                return await DriverReadAsync(request, "command.can_perform", new JsonObject { ["id"] = command.Id }, cancellationToken).ConfigureAwait(false);
            case "transport":
                return await DriverReadAsync(request, "transport.get", new JsonObject(), cancellationToken).ConfigureAwait(false);
            case "track":
                return operation.Action == "list"
                    ? await TrackListAsync(request, session, cancellationToken).ConfigureAwait(false)
                    : await TrackGetAsync(request, session, cancellationToken).ConfigureAwait(false);
            case "mixer":
                return await MixerGetAsync(request, args, session, cancellationToken).ConfigureAwait(false);
            case "plugin":
                return await PluginReadAsync(request, operation, session, cancellationToken).ConfigureAwait(false);
            case "direct":
                return await DirectReadAsync(request, args, session, cancellationToken).ConfigureAwait(false);
            case "project":
                return await ProjectGetAsync(request, cancellationToken).ConfigureAwait(false);
            case "midi.compose":
                var composition = _compositions.Execute(operation.Action, args, session);
                return BrokerResponse.Success(request.Id, composition, "midi_composition+smf", true,
                    sessionRevision: _driver.Revision, executionState: ExecutionState.NotApplicable);
            case "ui.inspect":
                var inspected = await _ui.InspectAsync(args, session).ConfigureAwait(false);
                return BrokerResponse.Success(request.Id, inspected, "ui_automation+print_window+windows_ocr", true,
                    warnings: ["Inspection only describes the current UI; it does not verify an earlier action's postcondition. Element tokens expire after 30 seconds and one action."],
                    sessionRevision: _driver.Revision, executionState: ExecutionState.NotApplicable);
            case "batch":
                return BatchResumePlan(request, args, session);
            default:
                throw new PlanException(ErrorCodes.InvalidAction, $"No reader for '{operation.Key}'.");
        }
    }

    private async Task<BrokerResponse> DriverReadAsync(BrokerRequest request, string driverOperation, JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var reply = await _driver.SendAsync(driverOperation, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Ok) return DriverFailure(request, reply) with { ExecutionState = ExecutionState.NotApplicable };
        return BrokerResponse.Success(request.Id, reply.Data, "midi_remote", true, sessionRevision: reply.Revision,
            executionState: ExecutionState.NotApplicable);
    }

    private BrokerResponse DocumentOperation(BrokerRequest request, NormalizedOperation operation, string session)
    {
        if (operation.Risk == RiskLevel.Confirm)
        {
            if (string.IsNullOrWhiteSpace(request.ConfirmationId))
            {
                var proposal = _confirmations.Prepare(session, operation.Key, operation.Arguments, operation.Impact);
                return new BrokerResponse(request.Id, ResultStatus.RequiresConfirmation,
                    JsonSerializer.SerializeToNode(proposal, PipeProtocol.JsonOptions), null,
                    new OperationMetadata("safety_policy", false, null, null,
                        ["No change was made. Commit the same request with confirmation_id."], _driver.Revision),
                    ExecutionState.NotStarted);
            }
            if (!_confirmations.Consume(request.ConfirmationId, session, operation.Key, operation.Arguments))
                return NotStarted(request, ErrorCodes.ConfirmationMismatch,
                    "The confirmation is unknown, expired, used, or does not match this exact request.");
        }
        var data = _compositions.Execute(operation.Action, operation.Arguments, session);
        return BrokerResponse.Success(request.Id, data, "midi_composition+smf", true,
            warnings: ["Composition documents are session-scoped; render a .mid file for durable interchange."],
            sessionRevision: _driver.Revision, executionState: ExecutionState.Applied);
    }

    private async Task<BrokerResponse> TrackListAsync(BrokerRequest request, string session, CancellationToken cancellationToken)
    {
        var reply = await _driver.SendAsync("track.list", new JsonObject(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Ok) return DriverFailure(request, reply) with { ExecutionState = ExecutionState.NotApplicable };
        var epochs = CurrentEpochs();
        var channels = new JsonArray();
        foreach (var node in reply.Data?["channels"] as JsonArray ?? [])
        {
            var identity = ChannelIdentity(node);
            var handle = _targets.Issue(session, epochs, identity);
            channels.Add(handle.ToPublicJson());
        }
        var selected = SelectedIdentity(reply.Data?["selected"]);
        var data = new JsonObject
        {
            ["scope"] = reply.Data?["scope"]?.DeepClone() ?? "vst_mixer",
            ["bank_size"] = reply.Data?["bank_size"]?.DeepClone(),
            ["available_channel_count"] = channels.Count,
            ["bank_truncated"] = reply.Data?["bank_truncated"]?.DeepClone(),
            ["truncated"] = reply.Data?["bank_truncated"]?.DeepClone(),
            ["channels"] = channels,
            ["selected"] = selected.HasStableIdentity ? _targets.Issue(session, epochs, selected).ToPublicJson() : null,
            ["note"] = "Channels are the VST Mixer channels read through DirectAccess. bank_index is the channel's current position in the mixer (an address, not an identity). Handles bind the unique ID and mixer object ID and expire after 5 minutes or on a driver/project change."
        };
        return BrokerResponse.Success(request.Id, data, "midi_remote.mixer_bank", true, sessionRevision: reply.Revision,
            executionState: ExecutionState.NotApplicable);
    }

    private async Task<BrokerResponse> TrackGetAsync(BrokerRequest request, string session, CancellationToken cancellationToken)
    {
        var selected = await ReadSelectedAsync(cancellationToken).ConfigureAwait(false);
        var data = new JsonObject
        {
            ["selected"] = selected.HasStableIdentity ? _targets.Issue(session, CurrentEpochs(), selected).ToPublicJson() : null,
            ["selected_track"] = selected.Title
        };
        return BrokerResponse.Success(request.Id, data, "midi_remote", true, sessionRevision: _driver.Revision,
            executionState: ExecutionState.NotApplicable);
    }

    private async Task<BrokerResponse> MixerGetAsync(BrokerRequest request, JsonObject args, string session, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSelectorAsync(args, session, false, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            var mirror = await DriverReadAsync(request, "mixer.get", new JsonObject(), cancellationToken).ConfigureAwait(false);
            return mirror with
            {
                Metadata = mirror.Metadata! with
                {
                    Warnings = [.. mirror.Metadata.Warnings, "No target was given: this is the selected-track callback mirror, not a fresh read of a specific channel."]
                }
            };
        }
        var identity = resolved.Value.Identity;
        if (identity.Kind == TargetKinds.SelectedTrack)
            return await DriverReadAsync(request, "mixer.get", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var read = TargetArguments(identity);
        if (args["parameter_tag"] is { } tag) read["parameter_tag"] = tag.DeepClone();
        else read["parameter"] = args["parameter"]?.DeepClone() ?? "volume";
        var response = await DriverReadAsync(request, "mixer.get", read, cancellationToken).ConfigureAwait(false);
        if (response.Data is JsonObject data) data["target_handle"] = resolved.Value.Handle.Handle;
        return response;
    }

    private async Task<BrokerResponse> PluginReadAsync(BrokerRequest request, NormalizedOperation operation, string session,
        CancellationToken cancellationToken)
    {
        if (operation.Action == "catalog")
        {
            TargetIdentity? catalogTarget = null;
            if (operation.Arguments["target_handle"] is not null || operation.Arguments["target_scope"] is not null || operation.Arguments["bank_index"] is not null)
                catalogTarget = (await ResolveSelectorAsync(operation.Arguments, session, true, cancellationToken).ConfigureAwait(false))!.Value.Identity;
            var catalog = await PluginCatalogAsync(catalogTarget, ReadLong(operation.Arguments["object_id"]), cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(catalog, PipeProtocol.JsonOptions);
            return catalog.Available
                ? BrokerResponse.Success(request.Id, data, catalog.Source, true, executionState: ExecutionState.NotApplicable)
                : BrokerResponse.Failure(request.Id, ResultStatus.Unsupported, ErrorCodes.PluginCatalogUnavailable,
                    catalog.UnavailableReason ?? "No installed plug-in catalog is available.", data, catalog.Source, ExecutionState.NotApplicable);
        }
        var args = operation.Arguments;
        if (operation.Action == "inspect_slot")
        {
            var resolved = await ResolveSelectorAsync(args, session, true, cancellationToken).ConfigureAwait(false);
            var read = TargetArguments(resolved!.Value.Identity);
            read["action"] = "inspect_slot";
            read["object_id"] = args["object_id"]?.DeepClone();
            return await DriverReadAsync(request, "plugin.get", read, cancellationToken).ConfigureAwait(false);
        }
        var snapshot = new JsonObject { ["root"] = args["root"]?.DeepClone() ?? "selected_track" };
        foreach (var key in new[] { "max_nodes", "max_depth", "max_parameters", "include_parameters" })
            if (args[key] is { } value) snapshot[key] = value.DeepClone();
        return await DriverReadAsync(request, "plugin.get", snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BrokerResponse> DirectReadAsync(BrokerRequest request, JsonObject args, string session, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSelectorAsync(args, session, false, cancellationToken).ConfigureAwait(false);
        var read = new JsonObject();
        foreach (var key in new[] { "root", "max_nodes", "max_depth", "max_parameters", "include_parameters", "object_id", "parameter_tag" })
            if (args[key] is { } value) read[key] = value.DeepClone();
        if (resolved is not null) foreach (var pair in TargetArguments(resolved.Value.Identity)) read[pair.Key] = pair.Value?.DeepClone();
        return await DriverReadAsync(request, "direct.get", read, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BrokerResponse> ProjectGetAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var process = _processes.Inspect();
        var data = new JsonObject
        {
            ["cubase_running"] = process.Running,
            ["project_title"] = NormalizeProjectTitle(process.WindowTitle),
            ["project_epoch"] = ProjectEpoch(),
            ["identity_basis"] = "Cubase process id + start time + main window title; the MIDI Remote API exposes no project path."
        };
        var reply = await _driver.SendAsync("project.get", new JsonObject(), cancellationToken: cancellationToken).ConfigureAwait(false);
        data["driver"] = reply.Ok ? reply.Data?.DeepClone() : new JsonObject { ["error"] = reply.Error?.Code };
        return BrokerResponse.Success(request.Id, data, "process+midi_remote", true, sessionRevision: _driver.Revision,
            executionState: ExecutionState.NotApplicable);
    }

    private BrokerResponse OperationStatus(BrokerRequest request, JsonObject args, string session)
    {
        var id = args["operation_id"]?.GetValue<string>() ?? throw new PlanException(ErrorCodes.InvalidArgument, "operation_id is required.");
        var entry = _journal.Get(session, id);
        if (entry is null)
            return NotStarted(request, "OPERATION_NOT_FOUND", "No operation with this operation_id is recorded for this client session.");
        return BrokerResponse.Success(request.Id, JsonSerializer.SerializeToNode(entry, PipeProtocol.JsonOptions),
            "execution_journal", true, executionState: ExecutionState.NotApplicable);
    }

    private async Task<BrokerResponse> StatusAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var process = _processes.Inspect();
        var driverOnline = process.Running && await _driver.TryHandshakeAsync(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
        var runtime = _driver.LoopMidiSnapshot;
        var ports = runtime?.ObservedPorts ?? _driver.EnumeratePorts().ToArray();
        var midiInput = ports.Any(port => port.IsInput && port.Name.Equals(CubaseConstants.OutputPortName, StringComparison.OrdinalIgnoreCase));
        var midiOutput = ports.Any(port => !port.IsInput && port.Name.Equals(CubaseConstants.InputPortName, StringComparison.OrdinalIgnoreCase));
        var restartRequired = process.Running && !driverOnline && runtime is { PortsAvailable: true, StartedByBroker: true };
        var hello = _driver.Hello;
        var contractOk = driverOnline && hello?.ContractVersion == CubaseConstants.DriverContractVersion;
        var connectionState = driverOnline ? contractOk ? "ready" : "driver_contract_mismatch"
            : runtime?.Installed == false ? "loopmidi_missing"
            : runtime?.AutostartResult is "invalid_executable" or "loopmidi_start_failed" ? "loopmidi_start_failed"
            : runtime?.PortsAvailable != true ? "ports_missing"
            : restartRequired ? "cubase_restart_required"
            : "driver_handshake_timeout";
        var warnings = new List<string>();
        if (!midiInput || !midiOutput) warnings.Add("The required loopMIDI port pair is incomplete.");
        if (process.Running && !driverOnline) warnings.Add("Cubase is running, but the CompleteBridge driver did not answer.");
        if (driverOnline && !contractOk) warnings.Add($"The loaded driver reports contract {hello?.ContractVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"}; writes are refused until driver contract {CubaseConstants.DriverContractVersion} is installed and reloaded.");
        if (restartRequired) warnings.Add("loopMIDI was started after Cubase; restart Cubase manually if MIDI Remote does not rebind the ports.");
        if (IsReleaseMismatch(request)) warnings.Add("The MCP client and Broker are running from different Releases.");
        if (CommandCatalogWarning is not null) warnings.Add(CommandCatalogWarning);
        var features = hello?.Capabilities as JsonObject;
        var api13 = ReadBool(features?["api_1_3_plugin_manager"]) == true || ReadBool(features?["api_1_3_command_can_perform"]) == true;
        if (!api13) warnings.Add("MIDI Remote API 1.3 features (command canPerform, plug-in slot assignment) were not reported by the loaded driver.");

        var snapshot = new CubaseStatusSnapshot(process.Running, process.Version, process.ProcessId,
            process.WindowTitle, true, midiInput, midiOutput, driverOnline, api13,
            _commands?.Commands.Count ?? 0, _commands?.CategoryCount ?? 0, _driver.Revision, warnings.ToArray(),
            connectionState, runtime?.Installed ?? File.Exists(LoopMidiRuntime.ExecutablePath),
            runtime?.ProcessRunning ?? false, runtime?.AutostartAttempted ?? false,
            runtime?.AutostartResult ?? "not_checked", runtime?.ConfiguredPorts ?? [], ports.ToArray(),
            restartRequired, request.ClientReleaseId, _buildIdentity.ReleaseId, IsReleaseMismatch(request),
            CubaseConstants.PipeProtocolVersion, hello?.ContractVersion, hello?.BuildId, _driver.Epoch, features?.DeepClone());
        return BrokerResponse.Success(request.Id, JsonSerializer.SerializeToNode(snapshot, PipeProtocol.JsonOptions),
            "broker+winmm", true, sessionRevision: _driver.Revision, executionState: ExecutionState.NotApplicable);
    }

    private BrokerResponse Capabilities(BrokerRequest request, JsonObject args)
    {
        var query = args["query"]?.GetValue<string>();
        var category = args["category"]?.GetValue<string>();
        var limit = ReadInt(args["limit"]) ?? 100;
        bool Matches(params string[] fields) => string.IsNullOrWhiteSpace(query)
            || fields.Any(field => field.Contains(query, StringComparison.OrdinalIgnoreCase));
        var descriptors = StaticCapabilities()
            .Where(item => Matches(item.Name, item.Category, item.Id))
            .Where(item => string.IsNullOrWhiteSpace(category) || item.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
        var api13Available = ReadBool((_driver.Hello?.Capabilities as JsonObject)?["api_1_3_command_can_perform"]) == true;
        var commands = (_commands?.Search(query, category, limit) ?? [])
            .Select(command => CapabilityRegistry.DescribeCommand(command, api13Available)).ToArray();
        var directRoots = CapabilityRegistry.DirectRoots()
            .Where(item => Matches(item.Id, item.Name))
            .Where(item => string.IsNullOrWhiteSpace(category) || "direct".Contains(category, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
        var uiActions = CapabilityRegistry.UiActions()
            .Where(item => Matches(item.Action, item.Target, item.SupportLevel))
            .Where(item => string.IsNullOrWhiteSpace(category) || "ui".Contains(category, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
        var midiActions = CapabilityRegistry.MidiCompositionActions()
            .Where(item => Matches(item.Action, item.Category, item.Description))
            .Where(item => string.IsNullOrWhiteSpace(category) || item.Category.Contains(category, StringComparison.OrdinalIgnoreCase)
                || "composition".Contains(category, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
        var data = new JsonObject
        {
            ["capabilities"] = JsonSerializer.SerializeToNode(descriptors, PipeProtocol.JsonOptions),
            ["commands"] = JsonSerializer.SerializeToNode(commands, PipeProtocol.JsonOptions),
            ["direct_roots"] = JsonSerializer.SerializeToNode(directRoots, PipeProtocol.JsonOptions),
            ["ui_actions"] = JsonSerializer.SerializeToNode(uiActions, PipeProtocol.JsonOptions),
            ["midi_composition_actions"] = JsonSerializer.SerializeToNode(midiActions, PipeProtocol.JsonOptions),
            ["command_total"] = _commands?.Commands.Count ?? 0,
            ["category_total"] = _commands?.CategoryCount ?? 0
        };
        return BrokerResponse.Success(request.Id, data, "catalog", true, sessionRevision: _driver.Revision,
            executionState: ExecutionState.NotApplicable);
    }

    private BrokerResponse AllCapabilities(BrokerRequest request)
    {
        var api13Available = ReadBool((_driver.Hello?.Capabilities as JsonObject)?["api_1_3_command_can_perform"]) == true;
        var commandCapabilities = _commands?.Commands
            .Select(command => CapabilityRegistry.DescribeCommand(command, api13Available)).ToArray() ?? [];
        var data = new JsonObject
        {
            ["schema_version"] = 3,
            ["summary"] = new JsonObject
            {
                ["domain_capabilities"] = StaticCapabilities().Length,
                ["command_capabilities"] = commandCapabilities.Length,
                ["classified_commands"] = commandCapabilities.Count(capability => capability.Classified),
                ["unclassified_commands"] = commandCapabilities.Count(capability => !capability.Classified),
                ["unclassified_effective_risk"] = RiskLevel.Confirm.ToString(),
                ["direct_roots"] = CapabilityRegistry.DirectRoots().Length,
                ["ui_actions"] = CapabilityRegistry.UiActions().Length,
                ["midi_composition_actions"] = CapabilityRegistry.MidiCompositionActions().Length,
                ["api13_available"] = api13Available,
                ["support_levels"] = "implemented | mock_validated | live_validated | unsupported | blocked_environment; command_backed means the binding exists, not that its effect was verified"
            },
            ["capabilities"] = JsonSerializer.SerializeToNode(StaticCapabilities(), PipeProtocol.JsonOptions),
            ["commands"] = JsonSerializer.SerializeToNode(commandCapabilities, PipeProtocol.JsonOptions),
            ["direct_roots"] = JsonSerializer.SerializeToNode(CapabilityRegistry.DirectRoots(), PipeProtocol.JsonOptions),
            ["ui_actions"] = JsonSerializer.SerializeToNode(CapabilityRegistry.UiActions(), PipeProtocol.JsonOptions),
            ["midi_composition_actions"] = JsonSerializer.SerializeToNode(CapabilityRegistry.MidiCompositionActions(), PipeProtocol.JsonOptions)
        };
        return BrokerResponse.Success(request.Id, data, "capability_registry", true,
            sessionRevision: _driver.Revision, executionState: ExecutionState.NotApplicable);
    }

    private BrokerResponse Commands(BrokerRequest request)
    {
        if (_commands is null)
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, "COMMAND_CATALOG_UNAVAILABLE",
                CommandCatalogWarning ?? "Key Commands.xml is unavailable.", executionState: ExecutionState.NotApplicable);
        return BrokerResponse.Success(request.Id,
            JsonSerializer.SerializeToNode(_commands.Commands, PipeProtocol.JsonOptions), "command_catalog", true,
            sessionRevision: _driver.Revision, executionState: ExecutionState.NotApplicable);
    }

    private async Task<BrokerResponse> DiagnosticsAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var status = await StatusAsync(request with { Operation = "status" }, cancellationToken).ConfigureAwait(false);
        var data = new JsonObject
        {
            ["status"] = JsonSerializer.SerializeToNode(status, PipeProtocol.JsonOptions),
            ["midi_ports"] = JsonSerializer.SerializeToNode(_driver.EnumeratePorts(), PipeProtocol.JsonOptions),
            ["midi_last_error"] = _driver.LastTransportError,
            ["event_state"] = _driver.EventState,
            ["driver_epoch"] = _driver.Epoch,
            ["stale_frames_discarded"] = _driver.StaleFramesDiscarded,
            ["driver_hello"] = JsonSerializer.SerializeToNode(_driver.Hello, PipeProtocol.JsonOptions),
            ["mutation_queue_waiting"] = _coordinator.Waiting,
            ["active_target_handles"] = _targets.Count,
            ["journal_root"] = _journal.Root,
            ["paths"] = JsonSerializer.SerializeToNode(_paths, PipeProtocol.JsonOptions),
            ["client_release_id"] = request.ClientReleaseId,
            ["broker_release_id"] = _buildIdentity.ReleaseId,
            ["release_mismatch"] = IsReleaseMismatch(request),
            ["pipe_protocol_version"] = CubaseConstants.PipeProtocolVersion,
            ["pipe_name"] = CubaseConstants.PipeName,
            ["driver_contract_version_required"] = CubaseConstants.DriverContractVersion,
            ["broker_executable_path"] = _buildIdentity.ExecutablePath
        };
        return BrokerResponse.Success(request.Id, data, "broker_diagnostics", true, sessionRevision: _driver.Revision,
            executionState: ExecutionState.NotApplicable);
    }

    private bool IsReleaseMismatch(BrokerRequest request) =>
        !string.IsNullOrWhiteSpace(request.ClientReleaseId)
        && !string.IsNullOrWhiteSpace(_buildIdentity.ReleaseId)
        && !request.ClientReleaseId.Equals(_buildIdentity.ReleaseId, StringComparison.Ordinal);

    private static CapabilityDescriptor[] StaticCapabilities() =>
    [
        new("status", "Cubase and bridge status", "system", "broker+winmm", RiskLevel.None, "implemented", [], true),
        new("commands", "Generated Key Command catalog (bindings exist; effects are not verified)", "commands", "midi_remote", RiskLevel.Confirm, "command_backed", ["CompleteBridge contract 2 active"], false),
        new("transport", "Play/stop/record, cycle, metronome, tempo, positions with explicit time_format; observed postconditions", "transport", "midi_remote", RiskLevel.Low, "mock_validated", ["CompleteBridge online", "State reported in this driver session"], false),
        new("track", "64-slot mixer bank listing with target handles; guarded selected-track commands; create/rename workflows", "track", "midi_remote+command+ui", RiskLevel.Low, "mock_validated", ["Project open"], false),
        new("mixer", "Target-handle parameter reads and writes with fresh read-back", "mixer", "midi_remote", RiskLevel.Low, "mock_validated", ["Project open", "MIDI Remote API 1.2 DirectAccess"], false),
        new("plugin", "Plug-in slot inspection and parameters; installed catalog, slot assignment and UID read-back through MIDI Remote API 1.3", "plugin", "midi_remote", RiskLevel.Confirm, "mock_validated", ["MIDI Remote API 1.3"], false),
        new("direct", "Target-bound DirectAccess reads and writes", "direct", "midi_remote", RiskLevel.Low, "mock_validated", ["MIDI Remote API 1.2+"], false),
        new("midi-compose", "Structured MIDI composition, SMF import/export, content comparison", "composition", "smf", RiskLevel.Low, "implemented", [], true),
        new("midi-import", "Causal import into Cubase with unique tagged track names (targets_observed); parts/content need owned-fixture export", "composition", "smf+command+ui", RiskLevel.Low, "implemented", ["Cubase running", "Transport stopped"], false),
        new("project", "Open, save, save_as with overwrite identity, bounded WAV export", "project", "command+ui+file", RiskLevel.Confirm, "implemented", ["Cubase running"], false),
        new("control-room", "Control Room DirectAccess (read)", "mixer", "midi_remote", RiskLevel.None, "implemented", ["Control Room configured"], false),
        new("ui", "Cubase-owned UI Automation, OCR and guarded coordinate/keyboard input", "ui", "uia_ocr_send_input", RiskLevel.Confirm, "best_effort", ["Visible Cubase window", "Fresh snapshot"], false),
        new("batch", "Planned batches (<=32, <=180 s) with dependency-aware stop rules and journaled prefix", "workflow", "broker", RiskLevel.Low, "mock_validated", [], false)
    ];
}
