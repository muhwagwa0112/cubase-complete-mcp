using System.IO;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

public sealed partial class CubaseController
{
    /// <summary>
    /// The resolved plan of one host mutation. <see cref="Targets"/> and <see cref="Preconditions"/> are hashed into
    /// the approval; <see cref="Observed"/> carries planning reads that must not invalidate an approval (for
    /// example the current value of the parameter about to be changed).
    /// </summary>
    private sealed class HostPlan
    {
        public RiskLevel Risk { get; set; }
        public string Impact { get; set; } = string.Empty;
        public TargetIdentity? Target { get; set; }
        public TargetHandle? Handle { get; set; }
        public JsonObject Targets { get; } = new();
        public JsonObject Preconditions { get; } = new();
        public JsonObject Observed { get; } = new();
        public FileIdentity? File { get; set; }
        public KeyCommandDescriptor? Command { get; set; }
        public ImportExpectation? Import { get; set; }
        public MidiCompositionDocument? Document { get; set; }
        public string[] BeforeChannelIds { get; set; } = [];

        public string Hash(NormalizedOperation operation) => CanonicalJson.Hash("plan", new JsonObject
        {
            ["operation"] = operation.Key, ["arguments"] = operation.Arguments.DeepClone(),
            ["targets"] = Targets.DeepClone(), ["preconditions"] = Preconditions.DeepClone()
        });

        public string TargetsHash() => CanonicalJson.Hash("targets", Targets);

        public JsonObject TargetsJson() => (JsonObject)Targets.DeepClone();

        public JsonObject ToJson(NormalizedOperation operation) => new()
        {
            ["operation"] = operation.Key, ["risk"] = Risk.ToString(), ["classified"] = operation.Classified,
            ["impact"] = Impact, ["arguments"] = operation.Arguments.DeepClone(), ["targets"] = Targets.DeepClone(),
            ["preconditions"] = Preconditions.DeepClone(), ["observed_at_planning"] = Observed.DeepClone()
        };
    }

    private async Task<JsonNode> ReadDriverAsync(string operation, JsonObject? arguments, CancellationToken cancellationToken)
    {
        var reply = await _driver.SendAsync(operation, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
            throw new PlanException(reply.Error?.Code ?? "DRIVER_ERROR", reply.Error?.Message ?? $"Cubase driver read '{operation}' failed.");
        return reply.Data ?? new JsonObject();
    }

    private async Task<JsonArray> ReadChannelsAsync(CancellationToken cancellationToken) =>
        (await ReadDriverAsync("track.list", new JsonObject(), cancellationToken).ConfigureAwait(false))["channels"] as JsonArray
            ?? [];

    private async Task<TargetIdentity> ReadSelectedAsync(CancellationToken cancellationToken)
    {
        var data = await ReadDriverAsync("track.get", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return SelectedIdentity(data["selected"]);
    }

    /// <summary>
    /// Resolves exactly one selector (target_handle, bank_index or target_scope=selected) to a host identity.
    /// A selected-scope target is pinned to the track selected now; when that track is visible in the mixer bank
    /// it is addressed through its channel so later selection changes cannot retarget the write.
    /// </summary>
    private async Task<(TargetIdentity Identity, TargetHandle Handle)?> ResolveSelectorAsync(JsonObject arguments,
        string session, bool required, CancellationToken cancellationToken)
    {
        var epochs = CurrentEpochs();
        if (arguments["target_handle"]?.GetValue<string>() is { } handleText)
        {
            var resolution = _targets.Resolve(handleText, session, epochs);
            if (!resolution.Ok) throw new PlanException(resolution.ErrorCode!, resolution.Message!);
            return (resolution.Handle!.Identity, resolution.Handle);
        }
        if (arguments["bank_index"] is { } bankNode)
        {
            var bankIndex = ReadInt(bankNode) ?? throw new PlanException(ErrorCodes.InvalidArgument, "bank_index must be an integer.");
            if (bankIndex is < 0 or > 63) throw new PlanException(ErrorCodes.InvalidArgument, "bank_index must be between 0 and 63.");
            var channel = (await ReadChannelsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(node => ReadInt(node?["bank_index"]) == bankIndex)
                ?? throw new PlanException(ErrorCodes.StaleTarget, $"No mixer channel is present at bank_index {bankIndex}.");
            var identity = ChannelIdentity(channel);
            return (identity, _targets.Issue(session, epochs, identity));
        }
        if (arguments["target_scope"]?.GetValue<string>() == "selected")
        {
            var selected = await ReadSelectedAsync(cancellationToken).ConfigureAwait(false);
            if (!selected.HasStableIdentity)
                throw new PlanException(ErrorCodes.TargetRequired, "No track is selected, or Cubase did not report its identity.");
            var channels = (await ReadChannelsAsync(cancellationToken).ConfigureAwait(false))
                .Where(node => node?["unique_id"]?.GetValue<string>() == selected.UniqueId).ToArray();
            var identity = channels.Length == 1 ? ChannelIdentity(channels[0]) with { Title = channels[0]!["title"]?.GetValue<string>() ?? selected.Title } : selected;
            return (identity, _targets.Issue(session, epochs, identity));
        }
        if (required)
            throw new PlanException(ErrorCodes.TargetRequired,
                "This change needs a target: pass target_handle (from cubase_track action=list), bank_index, or target_scope=selected.");
        return null;
    }

    private static JsonObject TargetArguments(TargetIdentity identity) => identity.Kind == TargetKinds.MixerChannel
        ? new JsonObject { ["target"] = ExpectedJson(identity) }
        : new JsonObject { ["expected_selected"] = new JsonObject { ["unique_id"] = identity.UniqueId, ["runtime_id"] = identity.RuntimeId } };

    private static void RequireName(JsonObject arguments)
    {
        var name = arguments["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) throw new PlanException(ErrorCodes.InvalidArgument, "name is required.");
        if (name.Any(char.IsControl)) throw new PlanException(ErrorCodes.InvalidArgument, "name must not contain control characters.");
        if (name.Length > 128) throw new PlanException(ErrorCodes.InvalidArgument, "name must be at most 128 characters.");
    }

    private static bool IsSelectionDependent(KeyCommandDescriptor command) =>
        command.Name.Contains("Selected", StringComparison.OrdinalIgnoreCase)
        || (command.Category.Equals("Project", StringComparison.OrdinalIgnoreCase)
            && command.Name.Contains("Track", StringComparison.OrdinalIgnoreCase));

    private async Task<HostPlan> BuildPlanAsync(NormalizedOperation operation, string session, string operationId,
        CancellationToken cancellationToken)
    {
        var plan = new HostPlan { Risk = operation.Risk, Impact = operation.Impact };
        var args = operation.Arguments;
        plan.Targets["project_epoch"] = ProjectEpoch();
        switch (operation.Family)
        {
            case "transport":
                await PlanTransportAsync(plan, operation, cancellationToken).ConfigureAwait(false);
                break;
            case "mixer":
            case "direct":
                await PlanParameterWriteAsync(plan, operation, session, cancellationToken).ConfigureAwait(false);
                break;
            case "plugin":
                await PlanPluginAsync(plan, operation, session, cancellationToken).ConfigureAwait(false);
                break;
            case "track":
                await PlanTrackAsync(plan, operation, session, cancellationToken).ConfigureAwait(false);
                break;
            case "command":
                plan.Command = CommandLookup(args) ?? throw new PlanException(ErrorCodes.CommandNotFound, "Supply a valid command id or an exact category and name.");
                plan.Targets["command"] = plan.Command.Id;
                if (IsSelectionDependent(plan.Command))
                {
                    var resolved = await ResolveSelectorAsync(args, session, true, cancellationToken).ConfigureAwait(false);
                    await PinSelectedAsync(plan, resolved!.Value, cancellationToken).ConfigureAwait(false);
                }
                break;
            case "project":
                await PlanProjectAsync(plan, operation, cancellationToken).ConfigureAwait(false);
                break;
            case "midi.compose":
                await PlanMidiImportAsync(plan, operation, session, operationId, cancellationToken).ConfigureAwait(false);
                break;
            case "ui.invoke":
                plan.Targets["snapshot_id"] = args["snapshot_id"]?.DeepClone();
                plan.Targets["element_token"] = args["element_token"]?.DeepClone();
                break;
            default:
                throw new PlanException(ErrorCodes.InvalidAction, $"No host plan exists for '{operation.Key}'.");
        }
        return plan;
    }

    /// <summary>Selection-dependent commands may only run when the planned track is the selected track right now.</summary>
    private async Task PinSelectedAsync(HostPlan plan, (TargetIdentity Identity, TargetHandle Handle) resolved,
        CancellationToken cancellationToken)
    {
        var selected = await ReadSelectedAsync(cancellationToken).ConfigureAwait(false);
        if (!selected.HasStableIdentity || selected.UniqueId != resolved.Identity.UniqueId
            || (resolved.Identity.RuntimeId is not null && selected.RuntimeId != resolved.Identity.RuntimeId))
            throw new PlanException(ErrorCodes.TargetNotSelected,
                "This command acts on the selected track, and the planned track is not selected. Select it in Cubase (or use target_scope=selected) and plan again; the broker never changes the selection implicitly.");
        plan.Target = resolved.Identity;
        plan.Handle = resolved.Handle;
        plan.Targets["selected"] = new JsonObject { ["unique_id"] = selected.UniqueId, ["runtime_id"] = selected.RuntimeId };
        // Identity is the unique/runtime ID pair; titles and bank slots may change without changing the object.
        plan.Targets["target"] = TargetForHash(resolved.Identity);
    }

    private async Task PlanTransportAsync(HostPlan plan, NormalizedOperation operation, CancellationToken cancellationToken)
    {
        var snapshot = await ReadDriverAsync("transport.get", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var state = snapshot["state"] as JsonObject ?? new JsonObject();
        plan.Observed["transport"] = state.DeepClone();
        var recording = ReadBool(state["recording"]);
        if (operation.Action is "stop" or "record" or "record_start" or "record_stop")
            plan.Preconditions["recording"] = recording;
        if (operation.Action == "stop" && recording == true)
        {
            plan.Risk = RiskLevel.Confirm;
            plan.Impact = "Stop playback while Cubase is recording; the current recording pass ends.";
        }
        if (operation.Action is "position" or "left_locator" or "right_locator")
        {
            if (operation.Arguments.ContainsKey("value"))
                throw new PlanException(ErrorCodes.InvalidArgument,
                    "Positions use position_text with an explicit time_format; the numeric value field is ambiguous in protocol 2.");
            if (operation.Arguments["position_text"] is null || operation.Arguments["time_format"] is null)
                throw new PlanException(ErrorCodes.InvalidArgument, "position_text and time_format are required.");
        }
    }

    private async Task PlanParameterWriteAsync(HostPlan plan, NormalizedOperation operation, string session,
        CancellationToken cancellationToken)
    {
        var args = operation.Arguments;
        var resolved = await ResolveSelectorAsync(args, session, true, cancellationToken).ConfigureAwait(false);
        plan.Target = resolved!.Value.Identity;
        plan.Handle = resolved.Value.Handle;
        plan.Targets["target"] = TargetForHash(plan.Target);
        if (operation.Family == "mixer" && args["parameter"] is null && args["parameter_tag"] is null)
            throw new PlanException(ErrorCodes.InvalidArgument, "parameter (volume, pan, mute, solo, monitor, record_enable) or parameter_tag is required.");
        if (operation.Family == "direct" && (args["object_id"] is null || args["parameter_tag"] is null))
            throw new PlanException(ErrorCodes.InvalidArgument, "object_id and parameter_tag are required for direct writes.");
        if (plan.Target.Kind == TargetKinds.SelectedTrack && operation.Family == "mixer" && args["parameter_tag"] is not null)
            throw new PlanException(ErrorCodes.TargetContextUnsafe,
                "The selected track is not visible in the 64-channel mixer bank, so a parameter_tag write cannot be bound to it. Use a named parameter or show the channel in the MixConsole.");
        var values = new[] { "value", "process_value", "plain_value", "display_value" }.Count(args.ContainsKey);
        if (values != 1) throw new PlanException(ErrorCodes.InvalidArgument, "Supply exactly one of value, process_value, plain_value or display_value.");
        foreach (var key in new[] { "value", "process_value", "plain_value" })
            if (args[key] is { } node && ReadDouble(node) is { } number && !double.IsFinite(number))
                throw new PlanException(ErrorCodes.InvalidArgument, $"{key} must be finite.");
    }

    private static JsonObject TargetForHash(TargetIdentity identity) => new()
    {
        ["kind"] = identity.Kind, ["unique_id"] = identity.UniqueId, ["runtime_id"] = identity.RuntimeId
    };

    private async Task PlanPluginAsync(HostPlan plan, NormalizedOperation operation, string session, CancellationToken cancellationToken)
    {
        var args = operation.Arguments;
        var resolved = await ResolveSelectorAsync(args, session, true, cancellationToken).ConfigureAwait(false);
        plan.Target = resolved!.Value.Identity;
        plan.Handle = resolved.Value.Handle;
        plan.Targets["target"] = TargetForHash(plan.Target);
        if (args["object_id"] is null) throw new PlanException(ErrorCodes.InvalidArgument, "object_id of the plug-in slot is required.");
        if (operation.Action == "set_parameter")
        {
            if (args["parameter_tag"] is null) throw new PlanException(ErrorCodes.InvalidArgument, "parameter_tag is required.");
            return;
        }
        var uid = args["plugin_uid"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uid)) throw new PlanException(ErrorCodes.InvalidArgument, "plugin_uid is required.");
        var catalog = await PluginCatalogAsync(plan.Target, ReadLong(args["object_id"]), cancellationToken).ConfigureAwait(false);
        if (!catalog.Available)
            throw new PlanException(ErrorCodes.PluginCatalogUnavailable, catalog.UnavailableReason
                ?? "No verifiable installed plug-in catalog is available, so plugin_uid cannot be validated.");
        var entry = catalog.Entries.FirstOrDefault(item => item.Uid.Equals(uid, StringComparison.Ordinal))
            ?? catalog.Entries.FirstOrDefault(item => NormalizePluginUid(item.Uid) == NormalizePluginUid(uid));
        if (entry is null || !entry.Available)
            throw new PlanException(ErrorCodes.InvalidArgument, "plugin_uid is not an available entry of the installed plug-in catalog.");
        var inspect = TargetArguments(plan.Target);
        inspect["action"] = "inspect_slot";
        inspect["object_id"] = args["object_id"]!.DeepClone();
        var slot = await ReadDriverAsync("plugin.get", inspect, cancellationToken).ConfigureAwait(false);
        plan.Preconditions["slot_unique_id"] = slot["slot"]?["unique_id"]?.DeepClone();
        plan.Preconditions["slot_title"] = slot["slot"]?["title"]?.DeepClone();
        plan.Targets["plugin"] = new JsonObject { ["uid"] = entry.Uid, ["name"] = entry.Name, ["vendor"] = entry.Vendor };
        plan.Impact = $"Assign '{entry.Name}' to the slot currently holding '{slot["slot"]?["title"]}'. Existing plug-in contents in that slot are discarded"
            + (args["confirm_replace"]?.GetValue<bool>() == true ? " without a host prompt." : "; Cubase may prompt first.");
        plan.Risk = RiskLevel.Confirm;
    }

    private async Task PlanTrackAsync(HostPlan plan, NormalizedOperation operation, string session, CancellationToken cancellationToken)
    {
        var args = operation.Arguments;
        switch (operation.Action)
        {
            case "create":
                RequireName(args);
                var type = args["track_type"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                    ?? throw new PlanException(ErrorCodes.InvalidArgument, "track_type is required (midi, instrument or audio).");
                plan.Command = RequireCommand("AddTrack", TrackTypeCommand(type));
                plan.Targets["track_type"] = type;
                plan.BeforeChannelIds = (await ReadChannelsAsync(cancellationToken).ConfigureAwait(false))
                    .Select(node => node?["unique_id"]?.GetValue<string>() ?? string.Empty).ToArray();
                break;
            case "rename":
                RequireName(args);
                var rename = await ResolveSelectorAsync(args, session, true, cancellationToken).ConfigureAwait(false);
                await PinSelectedAsync(plan, rename!.Value, cancellationToken).ConfigureAwait(false);
                plan.Command = RequireCommand("Edit", "Rename First Selected Track");
                break;
            case "delete":
            case "duplicate":
            case "freeze":
            case "enable":
                var resolved = await ResolveSelectorAsync(args, session, true, cancellationToken).ConfigureAwait(false);
                await PinSelectedAsync(plan, resolved!.Value, cancellationToken).ConfigureAwait(false);
                plan.Command = operation.Action switch
                {
                    "delete" => RequireCommand("Project", "Remove Selected Tracks"),
                    "duplicate" => RequireCommand("Project", "Duplicate Tracks"),
                    "freeze" => RequireCommand("Edit", "Freeze/Unfreeze Selected Tracks"),
                    _ => RequireCommand("Audio", "Disable/Enable Track")
                };
                plan.Impact = operation.Action == "delete"
                    ? $"Delete track '{resolved.Value.Identity.Title}' (unique ID {resolved.Value.Identity.UniqueId}). Its events and automation are removed."
                    : $"{operation.Action} track '{resolved.Value.Identity.Title}'.";
                plan.BeforeChannelIds = (await ReadChannelsAsync(cancellationToken).ConfigureAwait(false))
                    .Select(node => node?["unique_id"]?.GetValue<string>() ?? string.Empty).ToArray();
                break;
            case "next":
            case "previous":
                plan.Observed["selected_before"] = (await ReadSelectedAsync(cancellationToken).ConfigureAwait(false)).ToJson();
                break;
        }
    }

    internal static string TrackTypeCommand(string type) => type switch
    {
        "audio" => "Audio", "audio_mono" => "Audio Mono", "instrument" => "Instrument", "sampler" => "Sampler",
        "drum" => "Drum", "midi" => "MIDI", "fx" => "FX Channel", "group" => "Group Channel", "vca" => "VCA Fader",
        "marker" => "Marker", "folder" => "Folder", "tempo" => "Tempo", "signature" => "Signature",
        "arranger" => "Arranger", "chord" => "Chord", "video" => "Video",
        _ => throw new PlanException("TRACK_TYPE_UNSUPPORTED", $"Unknown track_type '{type}'.")
    };

    private Task PlanProjectAsync(HostPlan plan, NormalizedOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = operation.Arguments;
        var overwrite = args["overwrite"]?.GetValue<bool>() == true;
        var title = NormalizeProjectTitle(_processes.Inspect().WindowTitle);
        plan.Preconditions["project_title"] = title;
        switch (operation.Action)
        {
            case "open":
                var openPath = WorkflowPathPolicy.RequireAbsolute(args["path"]?.GetValue<string>(), ".cpr", mustExist: true);
                plan.File = FileIdentity.Capture(openPath);
                plan.Preconditions["file"] = plan.File.ToJson();
                plan.Command = RequireCommand("File", "Open");
                plan.Impact = $"Open {openPath}. The currently active project '{title}' is deactivated; Cubase may ask about unsaved changes, which the broker does not answer.";
                break;
            case "save_as":
                var saveAsPath = WorkflowPathPolicy.RequireAbsolute(args["path"]?.GetValue<string>(), ".cpr", mustExist: false);
                plan.File = WorkflowPathPolicy.PlanOutput(saveAsPath, overwrite);
                plan.Preconditions["file"] = plan.File.ToJson();
                plan.Command = RequireCommand("File", "Save As");
                if (plan.File.Exists) plan.Impact = $"Overwrite {saveAsPath} ({plan.File.Bytes} bytes, sha256 {plan.File.Sha256}) with the open project '{title}'.";
                break;
            case "save":
                var savePath = WorkflowPathPolicy.RequireAbsolute(args["path"]?.GetValue<string>(), ".cpr", mustExist: true);
                if (!title.Contains(Path.GetFileNameWithoutExtension(savePath), StringComparison.OrdinalIgnoreCase))
                    throw new PlanException(ErrorCodes.TargetChanged,
                        $"The open project '{title}' does not match {Path.GetFileName(savePath)}; save refuses to write another project's file.");
                plan.File = FileIdentity.Capture(savePath);
                plan.Preconditions["file"] = plan.File.ToJson();
                plan.Command = RequireCommand("File", "Save");
                plan.Impact = $"Overwrite {savePath} with the current state of '{title}'.";
                break;
            case "export_audio":
                var wavPath = WorkflowPathPolicy.RequireAbsolute(args["path"]?.GetValue<string>(), ".wav", mustExist: false);
                plan.File = WorkflowPathPolicy.PlanOutput(wavPath, overwrite);
                plan.Preconditions["file"] = plan.File.ToJson();
                var seconds = ReadDouble(args["expected_duration_seconds"]);
                if (seconds is null || seconds <= 0 || !double.IsFinite(seconds.Value))
                    throw new PlanException(ErrorCodes.InvalidArgument,
                        "expected_duration_seconds (the locator range length) is required to verify the rendered WAV.");
                plan.Command = RequireCommand("File", "Export Audio Mixdown");
                plan.Targets["perform_command"] = RequireCommand("Audio Export", "Perform Audio Export").Id;
                plan.Targets["profile"] = "wav_pcm_stereo_48000_24";
                plan.Impact = $"Open Export Audio Mixdown, name the file '{Path.GetFileNameWithoutExtension(wavPath)}', verify by read-back that it is a "
                    + "48 kHz/24-bit WAV in the project Mixdown folder, then export the locator range once.";
                break;
            case "new":
                plan.Command = RequireCommand("File", "New");
                break;
            case "close":
                plan.Command = RequireCommand("File", "Close");
                break;
        }
        return Task.CompletedTask;
    }

    private async Task PlanMidiImportAsync(HostPlan plan, NormalizedOperation operation, string session, string operationId,
        CancellationToken cancellationToken)
    {
        var args = operation.Arguments;
        var documentId = args["document_id"]?.GetValue<string>() ?? throw new PlanException(ErrorCodes.InvalidArgument, "document_id is required.");
        plan.Document = _compositions.LoadDocument(documentId, session);
        plan.Import = MidiImportCausality.Plan(plan.Document, operationId);
        if (plan.Import.Tracks.Count == 0) throw new PlanException(ErrorCodes.InvalidArgument, "The document has no importable tracks.");
        plan.Command = RequireCommand("File", "Import MIDI File");
        var transport = (await ReadDriverAsync("transport.get", new JsonObject(), cancellationToken).ConfigureAwait(false))["state"] as JsonObject
            ?? new JsonObject();
        var playing = ReadBool(transport["playing"]);
        var recording = ReadBool(transport["recording"]);
        plan.Preconditions["transport_playing"] = playing;
        plan.Preconditions["transport_recording"] = recording;
        plan.Preconditions["source_digest"] = plan.Import.SourceDigest;
        var active = playing == true || recording == true;
        if (active && args["stop_transport"]?.GetValue<bool>() != true)
            throw new PlanException(ErrorCodes.TransportActive,
                "Cubase is playing or recording. Import needs a stopped transport; pass stop_transport=true to include the stop in a confirmed plan.");
        if (playing is null || recording is null)
            throw new PlanException(ErrorCodes.StateUnknown, "Cubase has not reported the transport state in this driver session.");
        plan.Targets["verification_tag"] = plan.Import.VerificationTag;
        plan.Targets["track_mapping"] = new JsonArray(plan.Import.Tracks.Select(track => (JsonNode?)new JsonObject
        {
            ["source_index"] = track.SourceIndex, ["original_name"] = track.OriginalName, ["created_name"] = track.ImportedName
        }).ToArray());
        plan.Targets["conductor_skipped"] = plan.Import.ConductorSkipped;
        plan.Targets["conductor_reason"] = plan.Import.ConductorReason;
        plan.BeforeChannelIds = (await ReadChannelsAsync(cancellationToken).ConfigureAwait(false))
            .Select(node => node?["unique_id"]?.GetValue<string>() ?? string.Empty).ToArray();
        plan.Impact = (active ? "Stop Cubase transport, then import " : "Import ")
            + $"{plan.Import.Tracks.Count} new MIDI track(s) named with the verification tag [{plan.Import.VerificationTag}] into the open project.";
        if (active) plan.Risk = RiskLevel.Confirm;
    }
}
