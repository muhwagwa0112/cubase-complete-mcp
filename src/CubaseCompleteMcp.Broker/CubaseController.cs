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
    private readonly KeyCommandCatalog? _commands;

    public CubaseController(CubasePaths paths, ConfirmationStore confirmations,
        CubaseProcessInspector processes, WinMmMidiTransport midi, CubaseMidiSession session,
        CubaseUiAutomation ui)
    {
        _paths = paths;
        _confirmations = confirmations;
        _processes = processes;
        _midi = midi;
        _session = session;
        _ui = ui;
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
            var effectiveRisk = Classify(operation, request.Arguments);
            if (!parentConfirmed && effectiveRisk == RiskLevel.Confirm)
            {
                if (string.IsNullOrWhiteSpace(request.ConfirmationId)
                    || !_confirmations.Consume(request.ConfirmationId, operation, request.Arguments))
                {
                    var proposal = _confirmations.Prepare(operation, request.Arguments,
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
                "command" => await CommandAsync(request, cancellationToken).ConfigureAwait(false),
                "transport" => await DriverAsync(request, MapReadWrite("transport", request.Arguments), cancellationToken).ConfigureAwait(false),
                "track" => await TrackAsync(request, cancellationToken).ConfigureAwait(false),
                "mixer" => await DriverAsync(request, MapReadWrite("mixer", request.Arguments), cancellationToken).ConfigureAwait(false),
                "plugin" => await DriverAsync(request, MapReadWrite("plugin", request.Arguments), cancellationToken).ConfigureAwait(false),
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
        var commands = _commands?.Search(query, category, limit) ?? [];
        var data = new JsonObject
        {
            ["capabilities"] = JsonSerializer.SerializeToNode(descriptors, PipeProtocol.JsonOptions),
            ["commands"] = JsonSerializer.SerializeToNode(commands, PipeProtocol.JsonOptions),
            ["command_total"] = _commands?.Commands.Count ?? 0,
            ["category_total"] = _commands?.CategoryCount ?? 0
        };
        return BrokerResponse.Success(request.Id, data, "catalog", true, sessionRevision: _session.Revision);
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

    private async Task<BrokerResponse> UiInspectAsync(BrokerRequest request)
    {
        var data = await _ui.InspectAsync(request.Arguments).ConfigureAwait(false);
        return BrokerResponse.Success(request.Id, data, "ui_automation+print_window+windows_ocr", true,
            warnings: ["Custom-rendered plugin controls may not expose stable UI Automation elements; OCR coordinates must be refreshed after every input."],
            sessionRevision: _session.Revision);
    }

    private BrokerResponse UiInvoke(BrokerRequest request)
    {
        var data = _ui.Invoke(request.Arguments ?? throw new ArgumentException("UI arguments are required."));
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
                item["arguments"] as JsonObject, null);
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
        var reply = await _session.SendAsync(driverOperation, driverArguments ?? request.Arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return BrokerResponse.Failure(request.Id,
                reply.Error?.Code == "DRIVER_OFFLINE" ? ResultStatus.Offline : ResultStatus.Error,
                reply.Error?.Code ?? "DRIVER_ERROR", reply.Error?.Message ?? "Cubase driver operation failed.",
                reply.Error?.Details, "midi_remote");
        }

        return BrokerResponse.Success(request.Id, reply.Data, "midi_remote", reply.Verified,
            warnings: reply.Verified ? [] : ["The driver accepted the operation but did not observe a conclusive postcondition."],
            sessionRevision: reply.Revision);
    }

    private static string MapReadWrite(string family, JsonObject? arguments)
    {
        var action = arguments?["action"]?.GetValue<string>()?.ToLowerInvariant() ?? "get";
        return action is "get" or "list" or "inspect" or "status" ? family + ".get" : family + ".set";
    }

    private static RiskLevel Classify(string operation, JsonObject? arguments)
    {
        var action = arguments?["action"]?.GetValue<string>()?.ToLowerInvariant();
        if (operation == "track" && action == "delete") return RiskLevel.Confirm;
        if (operation == "transport" && action == "record" && arguments?["enabled"]?.GetValue<bool>() != false) return RiskLevel.Confirm;
        if (operation == "project" && action is "overwrite" or "close_unsaved") return RiskLevel.Confirm;
        if (operation == "plugin" && action == "bulk_replace") return RiskLevel.Confirm;
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
        new("commands", "2,777 Key Command catalog", "commands", "midi_remote", RiskLevel.Low, "generated", ["Cubase 15.0.20+", "CompleteBridge active"], false),
        new("transport", "Transport, locators, cycle, tempo", "transport", "midi_remote", RiskLevel.Low, "full", ["CompleteBridge online"], false),
        new("track", "Selected track and track commands", "track", "midi_remote", RiskLevel.Low, "partial", ["Project open"], false),
        new("mixer", "Mixer bank, EQ, sends and automation", "mixer", "midi_remote", RiskLevel.Low, "dynamic", ["Project open"], false),
        new("plugin", "DirectAccess plug-in slots and parameters", "plugin", "midi_remote", RiskLevel.Low, "dynamic", ["Cubase 15.0.20+"], false),
        new("project", "Project file and render workflows", "project", "command_or_ui", RiskLevel.Low, "mixed", ["Cubase running"], false),
        new("control-room", "Control Room DirectAccess", "mixer", "midi_remote", RiskLevel.Low, "dynamic", ["Control Room configured"], false),
        new("ui", "Cubase-owned UI Automation and screenshot inspection", "ui", "ui_automation", RiskLevel.Low, "best_effort", ["Visible Cubase window"], false),
        new("batch", "Sequential operations with stop-on-failure", "workflow", "broker", RiskLevel.Low, "full", [], true)
    ];
}
