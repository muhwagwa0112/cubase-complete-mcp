using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

public sealed class CubaseController
{
    private readonly CubasePaths _paths;
    private readonly ConfirmationStore _confirmations;
    private readonly CubaseProcessInspector _processes;
    private readonly WinMmMidiTransport _midi;
    private readonly CubaseMidiSession _session;
    private readonly CubaseUiAutomation _ui;
    private readonly MidiCompositionService _compositions;
    private readonly KeyCommandCatalog? _commands;

    public CubaseController(CubasePaths paths, ConfirmationStore confirmations,
        CubaseProcessInspector processes, WinMmMidiTransport midi, CubaseMidiSession session,
        CubaseUiAutomation ui, MidiCompositionService compositions)
    {
        _paths = paths;
        _confirmations = confirmations;
        _processes = processes;
        _midi = midi;
        _session = session;
        _ui = ui;
        _compositions = compositions;
        try
        {
            _commands = KeyCommandCatalog.Load(paths.KeyCommandsFile);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            CommandCatalogWarning = error.Message;
        }
    }

    public string? CommandCatalogWarning { get; }

    public Task<BrokerResponse> HandleAsync(BrokerRequest request, CancellationToken cancellationToken) =>
        HandleCoreAsync(request, false, cancellationToken);

    private async Task<BrokerResponse> HandleCoreAsync(BrokerRequest request, bool parentConfirmed,
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = request.Operation.Trim().ToLowerInvariant();
            var clientSessionId = request.ClientSessionId ?? "unscoped";
            var effectiveRisk = Classify(operation, request.Arguments);
            if (!parentConfirmed && effectiveRisk == RiskLevel.Confirm)
            {
                if (string.IsNullOrWhiteSpace(request.ConfirmationId)
                    || !_confirmations.Consume(request.ConfirmationId, clientSessionId, operation, request.Arguments))
                {
                    var proposal = _confirmations.Prepare(clientSessionId, operation, request.Arguments,
                        DescribeImpact(operation, request.Arguments));
                    return new BrokerResponse(request.Id, ResultStatus.RequiresConfirmation,
                        JsonSerializer.SerializeToNode(proposal, PipeProtocol.JsonOptions), null,
                        new OperationMetadata("safety_policy", false, null, null,
                            ["No change was made. Commit the same request with confirmation_id."], _session.Revision));
                }

                parentConfirmed = true;
            }

            return operation switch
            {
                "status" => await StatusAsync(request.Id, cancellationToken).ConfigureAwait(false),
                "capabilities.search" => Capabilities(request),
                "capabilities.list" => AllCapabilities(request),
                "commands.list" => Commands(request),
                "command" => await CommandAsync(request, cancellationToken).ConfigureAwait(false),
                "transport" => await DriverAsync(request, MapReadWrite("transport", request.Arguments), cancellationToken).ConfigureAwait(false),
                "track" => await TrackAsync(request, cancellationToken).ConfigureAwait(false),
                "mixer" => await DriverAsync(request, MapReadWrite("mixer", request.Arguments), cancellationToken).ConfigureAwait(false),
                "plugin" => await DriverAsync(request, MapReadWrite("plugin", request.Arguments), cancellationToken).ConfigureAwait(false),
                "direct" => await DriverAsync(request, MapReadWrite("direct", request.Arguments), cancellationToken).ConfigureAwait(false),
                "midi.compose" => await MidiCompositionAsync(request, cancellationToken).ConfigureAwait(false),
                "project" => await ProjectAsync(request, cancellationToken).ConfigureAwait(false),
                "ui.inspect" => await UiInspectAsync(request).ConfigureAwait(false),
                "ui.invoke" => UiInvoke(request),
                "batch" => await BatchAsync(request, parentConfirmed, cancellationToken).ConfigureAwait(false),
                "diagnostics" => await DiagnosticsAsync(request.Id, cancellationToken).ConfigureAwait(false),
                _ => BrokerResponse.Failure(request.Id, ResultStatus.Unsupported, "UNKNOWN_OPERATION",
                    $"Unknown broker operation '{request.Operation}'.")
            };
        }
        catch (OperationCanceledException)
        {
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, "CANCELLED", "The operation was cancelled.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or IOException or InvalidDataException or JsonException)
        {
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, "OPERATION_FAILED", error.Message);
        }
    }

    private async Task<BrokerResponse> StatusAsync(string id, CancellationToken cancellationToken)
    {
        var process = _processes.Inspect();
        var ports = _midi.EnumeratePorts();
        var midiInput = ports.Any(port => port.IsInput && port.Name.Equals(CubaseConstants.OutputPortName, StringComparison.OrdinalIgnoreCase));
        var midiOutput = ports.Any(port => !port.IsInput && port.Name.Equals(CubaseConstants.InputPortName, StringComparison.OrdinalIgnoreCase));
        var driverOnline = process.Running && await _session.TryHandshakeAsync(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        if (!midiInput || !midiOutput) warnings.Add("The required loopMIDI port pair is incomplete.");
        if (process.Running && !driverOnline) warnings.Add("Cubase is running, but the CompleteBridge v2 driver did not answer.");
        if (CommandCatalogWarning is not null) warnings.Add(CommandCatalogWarning);
        if (!IsApi13Version(process.Version)) warnings.Add("Cubase 15.0.20 or newer is required for MIDI Remote API v1.3 features.");

        var snapshot = new CubaseStatusSnapshot(process.Running, process.Version, process.ProcessId,
            process.WindowTitle, true, midiInput, midiOutput, driverOnline, IsApi13Version(process.Version),
            _commands?.Commands.Count ?? 0, _commands?.CategoryCount ?? 0, _session.Revision, warnings.ToArray());
        return BrokerResponse.Success(id, JsonSerializer.SerializeToNode(snapshot, PipeProtocol.JsonOptions),
            "broker+winmm", true, sessionRevision: _session.Revision);
    }

    private BrokerResponse Capabilities(BrokerRequest request)
    {
        var query = request.Arguments?["query"]?.GetValue<string>();
        var category = request.Arguments?["category"]?.GetValue<string>();
        var limit = request.Arguments?["limit"]?.GetValue<int>() ?? 100;
        var descriptors = StaticCapabilities()
            .Where(item => string.IsNullOrWhiteSpace(query)
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(category)
                || item.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
        var api13Available = IsApi13Version(_processes.Inspect().Version);
        var commands = (_commands?.Search(query, category, limit) ?? [])
            .Select(command => CapabilityRegistry.DescribeCommand(command, api13Available)).ToArray();
        var directRoots = CapabilityRegistry.DirectRoots()
            .Where(item => string.IsNullOrWhiteSpace(query)
                || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(category)
                || "direct".Contains(category, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
        var uiActions = CapabilityRegistry.UiActions()
            .Where(item => string.IsNullOrWhiteSpace(query)
                || item.Action.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Target.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.SupportLevel.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(category)
                || "ui".Contains(category, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
        var midiActions = CapabilityRegistry.MidiCompositionActions()
            .Where(item => string.IsNullOrWhiteSpace(query)
                || item.Action.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(category)
                || item.Category.Contains(category, StringComparison.OrdinalIgnoreCase)
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
        return BrokerResponse.Success(request.Id, data, "catalog", true, sessionRevision: _session.Revision);
    }

    private BrokerResponse AllCapabilities(BrokerRequest request)
    {
        var api13Available = IsApi13Version(_processes.Inspect().Version);
        var commandCapabilities = _commands?.Commands
            .Select(command => CapabilityRegistry.DescribeCommand(command, api13Available)).ToArray() ?? [];
        var data = new JsonObject
        {
            ["schema_version"] = 2,
            ["summary"] = new JsonObject
            {
                ["domain_capabilities"] = StaticCapabilities().Length,
                ["command_capabilities"] = commandCapabilities.Length,
                ["classified_commands"] = commandCapabilities.Length,
                ["unclassified_commands"] = 0,
                ["direct_roots"] = CapabilityRegistry.DirectRoots().Length,
                ["ui_actions"] = CapabilityRegistry.UiActions().Length,
                ["midi_composition_actions"] = CapabilityRegistry.MidiCompositionActions().Length,
                ["api13_available"] = api13Available
            },
            ["capabilities"] = JsonSerializer.SerializeToNode(StaticCapabilities(), PipeProtocol.JsonOptions),
            ["commands"] = JsonSerializer.SerializeToNode(commandCapabilities, PipeProtocol.JsonOptions),
            ["direct_roots"] = JsonSerializer.SerializeToNode(CapabilityRegistry.DirectRoots(), PipeProtocol.JsonOptions),
            ["ui_actions"] = JsonSerializer.SerializeToNode(CapabilityRegistry.UiActions(), PipeProtocol.JsonOptions),
            ["midi_composition_actions"] = JsonSerializer.SerializeToNode(CapabilityRegistry.MidiCompositionActions(), PipeProtocol.JsonOptions)
        };
        return BrokerResponse.Success(request.Id, data, "capability_registry", true,
            sessionRevision: _session.Revision);
    }

    private BrokerResponse Commands(BrokerRequest request)
    {
        if (_commands is null)
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, "COMMAND_CATALOG_UNAVAILABLE",
                CommandCatalogWarning ?? "Key Commands.xml is unavailable.");
        return BrokerResponse.Success(request.Id,
            JsonSerializer.SerializeToNode(_commands.Commands, PipeProtocol.JsonOptions), "command_catalog", true,
            sessionRevision: _session.Revision);
    }

    private async Task<BrokerResponse> CommandAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        if (_commands is null)
        {
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, "COMMAND_CATALOG_UNAVAILABLE",
                CommandCatalogWarning ?? "Key Commands.xml is unavailable.");
        }

        var args = request.Arguments ?? new JsonObject();
        KeyCommandDescriptor? command = null;
        if (args["id"]?.GetValue<string>() is { Length: > 0 } id) command = _commands.FindById(id);
        if (command is null && args["category"]?.GetValue<string>() is { Length: > 0 } category
            && args["name"]?.GetValue<string>() is { Length: > 0 } name)
        {
            command = _commands.Find(category, name);
        }

        if (command is null)
        {
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, "COMMAND_NOT_FOUND",
                "Supply a valid command id or an exact category and name.");
        }

        var driverArgs = new JsonObject { ["id"] = command.Id };
        var action = args["action"]?.GetValue<string>()?.ToLowerInvariant() ?? "can_perform";
        return await DriverAsync(request, action == "execute" ? "command.execute" : "command.can_perform",
            cancellationToken, driverArgs).ConfigureAwait(false);
    }

    private async Task<BrokerResponse> TrackAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var args = request.Arguments ?? new JsonObject();
        var action = args["action"]?.GetValue<string>()?.ToLowerInvariant() ?? "list";
        if (action is "list" or "inspect")
        {
            var direct = new JsonObject { ["root"] = "mixer", ["include_parameters"] = false, ["max_nodes"] = args["max_nodes"]?.DeepClone() ?? 256 };
            return await DriverAsync(request, "direct.get", cancellationToken, direct).ConfigureAwait(false);
        }
        if (action is "get" or "status" or "next" or "previous")
            return await DriverAsync(request, action is "get" or "status" ? "track.get" : "track.set", cancellationToken).ConfigureAwait(false);

        string[]? candidate = null;
        if (action == "create")
        {
            var type = args["track_type"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "audio";
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["audio"] = "Audio", ["audio_mono"] = "Audio Mono", ["instrument"] = "Instrument",
                ["sampler"] = "Sampler", ["drum"] = "Drum", ["midi"] = "MIDI", ["fx"] = "FX Channel",
                ["group"] = "Group Channel", ["vca"] = "VCA Fader", ["marker"] = "Marker", ["folder"] = "Folder",
                ["tempo"] = "Tempo", ["signature"] = "Signature", ["arranger"] = "Arranger", ["chord"] = "Chord", ["video"] = "Video"
            };
            if (!names.TryGetValue(type, out var name))
                return BrokerResponse.Failure(request.Id, ResultStatus.Unsupported, "TRACK_TYPE_UNSUPPORTED", $"Unknown track_type '{type}'.");
            candidate = ["AddTrack", name];
        }
        else if (action == "delete") candidate = ["Project", "Remove Selected Tracks"];
        else if (action == "duplicate") candidate = ["Project", "Duplicate Tracks"];
        else if (action == "freeze") candidate = ["Edit", "Freeze/Unfreeze Selected Tracks"];
        else if (action == "enable") candidate = ["Audio", "Disable/Enable Track"];
        else if (action == "rename") candidate = ["Edit", "Rename First Selected Track"];

        if (candidate is null || _commands?.Find(candidate[0], candidate[1]) is not { } command)
            return BrokerResponse.Failure(request.Id, ResultStatus.Unsupported, "TRACK_REQUIRES_UI",
                $"Track action '{action}' has no command binding; use cubase_ui_inspect/invoke.");
        return await DriverAsync(request, "command.execute", cancellationToken,
            new JsonObject { ["id"] = command.Id }).ConfigureAwait(false);
    }

    private async Task<BrokerResponse> ProjectAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var action = request.Arguments?["action"]?.GetValue<string>()?.ToLowerInvariant() ?? "get";
        if (action is "get" or "status") return await DriverAsync(request, "project.get", cancellationToken).ConfigureAwait(false);

        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["save"] = ["File", "Save"],
            ["save_as"] = ["File", "Save As"],
            ["new"] = ["File", "New"],
            ["open"] = ["File", "Open"],
            ["close"] = ["File", "Close"],
            ["export_audio"] = ["File", "Export Audio Mixdown"],
            ["import_audio"] = ["File", "Import Audio File"],
            ["import_midi"] = ["File", "Import MIDI File"],
            ["export_midi"] = ["File", "Export MIDI File"]
        };
        if (!aliases.TryGetValue(action, out var candidate) || _commands?.Find(candidate[0], candidate[1]) is not { } command)
        {
            return BrokerResponse.Failure(request.Id, ResultStatus.Unsupported, "PROJECT_REQUIRES_UI",
                $"Project action '{action}' has no verified command binding. Use cubase_ui_inspect/invoke.");
        }

        var commandArgs = new JsonObject { ["id"] = command.Id };
        return await DriverAsync(request, "command.execute", cancellationToken, commandArgs).ConfigureAwait(false);
    }

    private async Task<BrokerResponse> MidiCompositionAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var arguments = request.Arguments ?? new JsonObject();
        var action = arguments["action"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "list_documents";
        var session = request.ClientSessionId ?? "unscoped";
        if (action != "import_to_cubase")
        {
            var compositionData = _compositions.Execute(action, arguments, session);
            return BrokerResponse.Success(request.Id, compositionData, "midi_composition+smf", true,
                warnings: ["Composition documents are session-scoped; render a .mid file for durable interchange."],
                sessionRevision: _session.Revision);
        }

        if (_commands?.Find("File", "Import MIDI File") is not { } command)
            return BrokerResponse.Failure(request.Id, ResultStatus.Unsupported, "MIDI_IMPORT_COMMAND_UNAVAILABLE",
                "Cubase does not expose the Import MIDI File command in the current catalog.");
        var documentId = arguments["document_id"]?.GetValue<string>()
            ?? throw new ArgumentException("document_id is required.");
        var rendered = _compositions.RenderDocument(documentId, session);
        var transportBefore = await _session.SendAsync("transport.get", null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DriverReply? stopReply = null;
        if (transportBefore.Data?["playing"]?.GetValue<bool>() == true
            || transportBefore.Data?["recording"]?.GetValue<bool>() == true)
        {
            stopReply = await _session.SendAsync("transport.set", new JsonObject { ["action"] = "stop" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!stopReply.Ok)
                return BrokerResponse.Failure(request.Id, ResultStatus.Error, stopReply.Error?.Code ?? "MIDI_IMPORT_STOP_FAILED",
                    stopReply.Error?.Message ?? "Cubase transport could not be stopped before MIDI import.",
                    stopReply.Error?.Details, "midi_remote");
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        var before = await _session.SendAsync("direct.get", new JsonObject
        {
            ["root"] = "mixer", ["include_parameters"] = false, ["max_nodes"] = 1024, ["max_depth"] = 4
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonObject? recoveredProjectFolderDialog;
        try
        {
            recoveredProjectFolderDialog = await _ui.DismissProjectFolderDialogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { throw new InvalidOperationException($"MIDI import dialog recovery failed: {error.Message}", error); }
        DriverReply? commandReply = null;
        bool reusedFileDialog;
        bool anyFileDialog;
        try
        {
            reusedFileDialog = _ui.HasOpenMidiImportFileDialog();
            anyFileDialog = _ui.HasOpenFileDialog();
        }
        catch (Exception error) { throw new InvalidOperationException($"MIDI import preflight failed: {error.Message}", error); }
        if (anyFileDialog && !reusedFileDialog)
            throw new InvalidOperationException("MIDI import preflight failed: a non-MIDI Cubase file dialog is already open.");
        if (!reusedFileDialog)
        {
            commandReply = await _session.SendAsync("command.execute", new JsonObject { ["id"] = command.Id },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!commandReply.Ok)
                return BrokerResponse.Failure(request.Id, ResultStatus.Error, commandReply.Error?.Code ?? "MIDI_IMPORT_COMMAND_FAILED",
                    commandReply.Error?.Message ?? "Cubase rejected the Import MIDI File command.", commandReply.Error?.Details, "midi_remote");
        }
        JsonObject? projectChoiceBefore;
        try
        {
            projectChoiceBefore = await _ui.ChooseExistingProjectForMidiImportAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { throw new InvalidOperationException($"MIDI import project choice failed: {error.Message}", error); }
        JsonObject? menuFallback = null;
        if (projectChoiceBefore is null && !_ui.HasOpenMidiImportFileDialog())
        {
            try
            {
                menuFallback = await _ui.OpenMidiImportDialogViaMenuAsync(cancellationToken).ConfigureAwait(false);
                projectChoiceBefore = await _ui.ChooseExistingProjectForMidiImportAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) { throw new InvalidOperationException($"MIDI import menu fallback failed: {error.Message}", error); }
        }
        JsonObject dialog;
        try { dialog = await _ui.SubmitFileDialogAsync(rendered.Path, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { throw new InvalidOperationException($"MIDI import file submission failed: {error.Message}", error); }
        JsonObject? projectChoiceAfter;
        try
        {
            // Cubase can ask whether to create a new project either before the file picker or
            // after the selected MIDI file has been accepted, depending on its import settings.
            projectChoiceAfter = await _ui.ChooseExistingProjectForMidiImportAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { throw new InvalidOperationException($"MIDI import post-file project choice failed: {error.Message}", error); }
        var projectChoice = projectChoiceAfter ?? projectChoiceBefore;
        JsonObject? options;
        try
        {
            options = arguments["accept_import_options"]?.GetValue<bool>() == false
                ? null : await _ui.AcceptImportOptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { throw new InvalidOperationException($"MIDI import options failed: {error.Message}", error); }
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        bool fileDialogRemains;
        try { fileDialogRemains = _ui.HasOpenFileDialog(); }
        catch (Exception error) { throw new InvalidOperationException($"MIDI import postcondition failed: {error.Message}", error); }
        DriverReply after = default!;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            after = await _session.SendAsync("direct.get", new JsonObject
            {
                ["root"] = "mixer", ["include_parameters"] = false, ["max_nodes"] = 1024, ["max_depth"] = 4
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (after.Ok && after.Data?["root"]?.GetValue<string>() == "mixer") break;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        var expectedTrackNames = rendered.Document.Tracks.Skip(1).Select(track => track.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var beforeNodeCount = before.Data?["nodes"] is JsonArray beforeNodes ? beforeNodes.Count : 0;
        var afterNodeCount = after.Data?["nodes"] is JsonArray afterNodes ? afterNodes.Count : 0;
        var directNodeDelta = afterNodeCount - beforeNodeCount;
        var beforeObjectIds = DirectObjectIds(before.Data?["nodes"] as JsonArray);
        var afterObjectIds = DirectObjectIds(after.Data?["nodes"] as JsonArray);
        var addedObjectIds = afterObjectIds.Except(beforeObjectIds, StringComparer.Ordinal).ToArray();
        var directObservedTrackNames = after.Data?["nodes"] is JsonArray nodes
            ? nodes.OfType<JsonObject>().SelectMany(node => new[]
                {
                    node["title"]?.GetValue<string>(), node["unique_name"]?.GetValue<string>()
                })
                .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>()
                .Where(name => expectedTrackNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var observedTrackNames = new HashSet<string>(directObservedTrackNames, StringComparer.OrdinalIgnoreCase);
        JsonObject? uiPostcondition = null;
        if (directNodeDelta <= 0 && !expectedTrackNames.All(observedTrackNames.Contains))
        {
            for (var attempt = 0; attempt < 3 && !expectedTrackNames.All(observedTrackNames.Contains); attempt++)
            {
                if (attempt > 0) await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                uiPostcondition = await _ui.ObserveProjectTextAsync(cancellationToken).ConfigureAwait(false);
                var observedText = uiPostcondition["text"]?.GetValue<string>() ?? string.Empty;
                foreach (var expected in expectedTrackNames.Where(expected =>
                             observedText.Contains(expected, StringComparison.OrdinalIgnoreCase)))
                    observedTrackNames.Add(expected);
            }
        }
        var trackNamesVerified = expectedTrackNames.Length > 0 && expectedTrackNames.All(observedTrackNames.Contains);
        var graphGrowthVerified = directNodeDelta > 0 || addedObjectIds.Length > 0;
        var verified = !fileDialogRemains && after.Ok && (trackNamesVerified || graphGrowthVerified);
        var beforeSummary = DirectObservationSummary(before);
        var afterSummary = DirectObservationSummary(after);
        var uiPostconditionSummary = uiPostcondition is null ? null : new JsonObject
        {
            ["snapshot_id"] = uiPostcondition["snapshot_id"]?.DeepClone(),
            ["window_title"] = uiPostcondition["window_title"]?.DeepClone(),
            ["capture_method"] = uiPostcondition["capture_method"]?.DeepClone(),
            ["screenshot_path"] = uiPostcondition["screenshot_path"]?.DeepClone()
        };
        var data = new JsonObject
        {
            ["document"] = MidiCompositionService.Summary(rendered.Document), ["rendered_path"] = rendered.Path,
            ["rendered_bytes"] = rendered.Bytes.Length, ["project_choice"] = projectChoice?.DeepClone(),
            ["project_choice_before_file"] = projectChoiceBefore?.DeepClone(),
            ["project_choice_after_file"] = projectChoiceAfter?.DeepClone(),
            ["transport_before"] = transportBefore.Data?.DeepClone(), ["transport_stop"] = stopReply?.Data?.DeepClone(),
            ["recovered_project_folder_dialog"] = recoveredProjectFolderDialog,
            ["reused_open_file_dialog"] = reusedFileDialog,
            ["menu_fallback"] = menuFallback,
            ["file_dialog"] = dialog, ["import_options"] = options,
            ["file_dialog_remains"] = fileDialogRemains,
            ["expected_track_names"] = new JsonArray(expectedTrackNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["observed_track_names"] = new JsonArray(observedTrackNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["track_names_verified"] = trackNamesVerified,
            ["direct_node_delta"] = directNodeDelta,
            ["direct_added_object_count"] = addedObjectIds.Length,
            ["verification_method"] = trackNamesVerified ? "direct_access_or_project_ocr_track_name"
                : graphGrowthVerified ? "direct_access_mixer_graph_change" : "none",
            ["track_postcondition"] = uiPostconditionSummary,
            ["cubase_observation_before"] = beforeSummary.DeepClone(),
            ["cubase_observation_after"] = afterSummary.DeepClone(),
            ["import_submitted"] = true
        };
        return BrokerResponse.Success(request.Id, data,
            menuFallback is null ? "smf+midi_remote_command+ui_automation" : "smf+midi_remote_command+ui_automation_menu_fallback", verified,
            before: beforeSummary, after: afterSummary,
            warnings: verified && !trackNamesVerified
                ? ["Cubase verified the import by mixer-object graph growth; the exact track name was outside the visible/API-exposed region."]
                : verified ? [] : ["The MIDI file was submitted, but Cubase did not expose a conclusive imported-track postcondition."],
            sessionRevision: Math.Max(commandReply?.Revision ?? 0, after.Revision));
    }

    private static JsonObject DirectObservationSummary(DriverReply reply) => new()
    {
        ["ok"] = reply.Ok,
        ["root"] = reply.Data?["root"]?.DeepClone(),
        ["node_count"] = reply.Data?["nodes"] is JsonArray nodes ? nodes.Count : 0,
        ["truncated"] = reply.Data?["truncated"]?.DeepClone(),
        ["revision"] = reply.Revision
    };

    private static HashSet<string> DirectObjectIds(JsonArray? nodes) => nodes is null
        ? new HashSet<string>(StringComparer.Ordinal)
        : nodes.OfType<JsonObject>().Select(node => node["unique_id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToHashSet(StringComparer.Ordinal);

    private async Task<BrokerResponse> UiInspectAsync(BrokerRequest request)
    {
        var data = await _ui.InspectAsync(request.Arguments, request.ClientSessionId ?? "unscoped").ConfigureAwait(false);
        return BrokerResponse.Success(request.Id, data, "ui_automation+print_window+windows_ocr", true,
            warnings: ["Custom-rendered plugin controls may not expose stable UI Automation elements; OCR coordinates must be refreshed after every input."],
            sessionRevision: _session.Revision);
    }

    private BrokerResponse UiInvoke(BrokerRequest request)
    {
        var data = _ui.Invoke(request.Arguments ?? throw new ArgumentException("UI arguments are required."),
            request.ClientSessionId ?? "unscoped");
        return BrokerResponse.Success(request.Id, data, "ui_automation_or_send_input", false,
            warnings: ["Call cubase_ui_inspect after every UI action to verify the postcondition."],
            sessionRevision: _session.Revision);
    }

    private async Task<BrokerResponse> BatchAsync(BrokerRequest request, bool parentConfirmed,
        CancellationToken cancellationToken)
    {
        if (request.Arguments?["operations"] is not JsonArray operations || operations.Count == 0)
            throw new ArgumentException("batch.operations must be a non-empty array.");
        var responses = new JsonArray();
        foreach (var node in operations)
        {
            if (node is not JsonObject item) throw new ArgumentException("Every batch item must be an object.");
            var child = new BrokerRequest($"{request.Id}:{responses.Count}",
                item["operation"]?.GetValue<string>() ?? throw new ArgumentException("Batch item operation is required."),
                item["arguments"] as JsonObject, null, request.ClientSessionId);
            var response = await HandleCoreAsync(child, parentConfirmed, cancellationToken).ConfigureAwait(false);
            responses.Add(JsonSerializer.SerializeToNode(response, PipeProtocol.JsonOptions));
            if (response.Status is not ResultStatus.Ok and not ResultStatus.Unverified) break;
        }

        var allOk = responses.All(node => node?["status"]?.GetValue<string>() is "Ok" or "Unverified");
        return BrokerResponse.Success(request.Id, new JsonObject { ["responses"] = responses }, "batch", allOk,
            warnings: allOk ? [] : ["Batch stopped at the first failed operation. Automatic compensation is not available for every Cubase action."],
            sessionRevision: _session.Revision);
    }

    private async Task<BrokerResponse> DiagnosticsAsync(string id, CancellationToken cancellationToken)
    {
        var status = await StatusAsync(id + ":status", cancellationToken).ConfigureAwait(false);
        var ports = _midi.EnumeratePorts();
        var data = new JsonObject
        {
            ["status"] = JsonSerializer.SerializeToNode(status, PipeProtocol.JsonOptions),
            ["midi_ports"] = JsonSerializer.SerializeToNode(ports, PipeProtocol.JsonOptions),
            ["midi_last_error"] = _midi.LastError,
            ["event_state"] = _session.EventState,
            ["paths"] = JsonSerializer.SerializeToNode(_paths, PipeProtocol.JsonOptions)
        };
        return BrokerResponse.Success(id, data, "broker_diagnostics", true, sessionRevision: _session.Revision);
    }

    private async Task<BrokerResponse> DriverAsync(BrokerRequest request, string driverOperation,
        CancellationToken cancellationToken, JsonObject? driverArguments = null)
    {
        var isWrite = driverOperation.EndsWith(".set", StringComparison.Ordinal)
            || driverOperation.Equals("command.execute", StringComparison.Ordinal);
        JsonNode? before = isWrite ? _session.EventState : null;
        var reply = await _session.SendAsync(driverOperation, driverArguments ?? request.Arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return BrokerResponse.Failure(request.Id,
                reply.Error?.Code == "DRIVER_OFFLINE" ? ResultStatus.Offline : ResultStatus.Error,
                reply.Error?.Code ?? "DRIVER_ERROR", reply.Error?.Message ?? "Cubase driver operation failed.",
                reply.Error?.Details, "midi_remote");
        }

        var replyBefore = reply.Data?["before"]?.DeepClone() ?? before;
        var replyAfter = reply.Data?["after"]?.DeepClone() ?? (isWrite ? _session.EventState : null);
        return BrokerResponse.Success(request.Id, reply.Data, "midi_remote", reply.Verified,
            before: replyBefore, after: replyAfter,
            warnings: reply.Verified ? [] : ["The driver accepted the operation but did not observe a conclusive postcondition."],
            sessionRevision: reply.Revision);
    }

    private static string MapReadWrite(string family, JsonObject? arguments)
    {
        var action = arguments?["action"]?.GetValue<string>()?.ToLowerInvariant() ?? "get";
        return action is "get" or "list" or "inspect" or "status" ? family + ".get" : family + ".set";
    }

    private RiskLevel Classify(string operation, JsonObject? arguments)
    {
        var action = arguments?["action"]?.GetValue<string>()?.ToLowerInvariant();
        if (operation == "track" && action == "delete") return RiskLevel.Confirm;
        if (operation == "transport" && action == "record" && arguments?["enabled"]?.GetValue<bool>() != false) return RiskLevel.Confirm;
        if (operation == "project" && action is "overwrite" or "close_unsaved" or "save") return RiskLevel.Confirm;
        if (operation == "project" && action == "save_as" && arguments?["overwrite"]?.GetValue<bool>() == true) return RiskLevel.Confirm;
        if (operation == "project" && action is "close" or "new" or "revert") return RiskLevel.Confirm;
        if (operation == "plugin" && action == "bulk_replace") return RiskLevel.Confirm;
        if (operation == "midi.compose" && action is "list" or "list_documents" or "get" or "get_document" or "inspect_file") return RiskLevel.None;
        if (operation == "midi.compose" && action is "delete" or "delete_document" or "remove_track" or "remove_part" or "replace_range") return RiskLevel.Confirm;
        if (operation == "midi.compose" && action is "render" or "render_file" or "export_file"
            && arguments?["path"] is not null && arguments?["overwrite"]?.GetValue<bool>() == true) return RiskLevel.Confirm;
        if (operation == "ui.invoke" && action != "focus") return RiskLevel.Confirm;
        if (operation == "command" && action == "execute")
        {
            KeyCommandDescriptor? command = null;
            if (arguments?["id"]?.GetValue<string>() is { Length: > 0 } id) command = _commands?.FindById(id);
            if (command is null && arguments?["category"]?.GetValue<string>() is { Length: > 0 } category
                && arguments?["name"]?.GetValue<string>() is { Length: > 0 } name)
                command = _commands?.Find(category, name);
            if (command is not null) return CapabilityRegistry.ClassifyCommand(command);
        }
        if (operation == "batch" && arguments?["operations"] is JsonArray batch
            && batch.Any(item => item is JsonObject child && Classify(
                child["operation"]?.GetValue<string>() ?? string.Empty, child["arguments"] as JsonObject) == RiskLevel.Confirm))
            return RiskLevel.Confirm;
        return SafetyPolicy.Classify(operation, arguments);
    }

    private static string DescribeImpact(string operation, JsonObject? arguments) =>
        operation switch
        {
            "track" => "Delete the selected Cubase track. This may remove events and automation.",
            "transport" => "Start recording into the current Cubase project.",
            "project" => "Perform a project operation that can overwrite a file or discard unsaved changes.",
            "plugin" => "Replace multiple plug-in assignments.",
            "midi.compose" => "Delete or replace MIDI composition data, or overwrite an existing MIDI file.",
            "batch" => $"Run a batch containing {arguments?["operations"]?.AsArray().Count ?? 0} operations, including a destructive operation.",
            "ui.invoke" => "Invoke a potentially destructive Cubase UI action.",
            _ => $"Perform the confirm-required operation '{operation}'."
        };

    private static bool IsApi13Version(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var fields = version.Split(['.', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3
            && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var patch)
            && (major > 15 || major == 15 && (minor > 0 || patch >= CubaseConstants.MinimumCubaseBuildForApi13));
    }

    private static CapabilityDescriptor[] StaticCapabilities() =>
    [
        new("status", "Cubase and bridge status", "system", "broker+winmm", RiskLevel.None, "full", [], true),
        new("commands", "Complete generated Key Command catalog", "commands", "midi_remote", RiskLevel.Low, "command_backed", ["CompleteBridge active"], false),
        new("transport", "Transport, locators, cycle, tempo", "transport", "midi_remote", RiskLevel.Low, "full", ["CompleteBridge online"], false),
        new("track", "Selected track, dynamic enumeration and track commands", "track", "midi_remote_or_command", RiskLevel.Low, "dynamic", ["Project open"], false),
        new("mixer", "Mixer bank, EQ, sends and automation", "mixer", "midi_remote", RiskLevel.Low, "dynamic", ["Project open"], false),
        new("plugin", "DirectAccess plug-in slots and parameters", "plugin", "midi_remote", RiskLevel.Low, "dynamic", ["Project open", "15.0.20+ only for slot assignment"], false),
        new("direct", "All exposed DirectAccess object trees and parameters", "direct", "midi_remote", RiskLevel.Low, "dynamic", ["MIDI Remote API v1.2+"], false),
        new("midi-compose", "Structured MIDI composition, editing, SMF import/export and Cubase import", "composition", "smf+midi_remote+ui", RiskLevel.Low, "full", [], true),
        new("project", "Project file, import, export and render workflows", "project", "command_or_ui", RiskLevel.Low, "command_and_ui", ["Cubase running"], false),
        new("control-room", "Control Room DirectAccess", "mixer", "midi_remote", RiskLevel.Low, "dynamic", ["Control Room configured"], false),
        new("ui", "Cubase-owned UI Automation, OCR and guarded coordinate/keyboard input", "ui", "uia_ocr_send_input", RiskLevel.Confirm, "best_effort", ["Visible Cubase window", "Fresh snapshot"], false),
        new("batch", "Sequential operations with stop-on-failure", "workflow", "broker", RiskLevel.Low, "full", [], true)
    ];
}
