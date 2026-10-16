using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

public sealed class PlanException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class OperationScope
{
    /// <summary>Changes Cubase: serialized by the mutation lease, journaled, never replayed.</summary>
    public const string Host = "host";
    /// <summary>Changes only broker-side session documents (MIDI compositions).</summary>
    public const string Document = "document";
    /// <summary>Reads host or broker state.</summary>
    public const string Read = "read";
}

/// <summary>
/// One normalized request. Classification, hashing and dispatch all use this value, so an action cannot be
/// classified one way and dispatched another.
/// </summary>
public sealed record NormalizedOperation(
    string Family,
    string Action,
    JsonObject Arguments,
    string Scope,
    RiskLevel Risk,
    bool Classified,
    string Impact)
{
    public bool IsHostMutation => Scope == OperationScope.Host;
    public string Key => Family + "." + Action;
    public string ArgumentsHash => CanonicalJson.Hash("args:" + Key, Arguments);
}

/// <summary>
/// Shared normalizer and risk classifier for every operation, including batch children. Unknown operations and
/// actions are rejected instead of falling back to a read route or a raw command execution.
/// </summary>
public static class OperationPlanner
{
    private static readonly Dictionary<string, (string Default, string[] Reads, string[] Writes)> Families =
        new(StringComparer.Ordinal)
        {
            ["status"] = ("status", ["status"], []),
            ["capabilities.search"] = ("search", ["search"], []),
            ["capabilities.list"] = ("list", ["list"], []),
            ["commands.list"] = ("list", ["list"], []),
            ["diagnostics"] = ("status", ["status", "operation_status"], []),
            ["command"] = ("can_perform", ["can_perform"], ["execute"]),
            ["transport"] = ("get", ["get"], ["play", "stop", "record", "record_start", "record_stop", "cycle", "metronome", "tempo", "position", "left_locator", "right_locator"]),
            ["track"] = ("list", ["list", "get"], ["create", "rename", "delete", "duplicate", "freeze", "enable", "next", "previous"]),
            ["mixer"] = ("get", ["get"], ["set"]),
            ["plugin"] = ("list", ["list", "inspect_slot", "catalog"], ["set_parameter", "assign"]),
            ["direct"] = ("list", ["list", "get"], ["set"]),
            ["project"] = ("get", ["get"], ["open", "save", "save_as", "export_audio", "new", "close"]),
            ["midi.compose"] = ("list_documents", ["list", "list_documents", "get", "get_document", "inspect_file", "compare_file"], []),
            ["ui.inspect"] = ("inspect", ["inspect"], []),
            ["ui.invoke"] = ("", [], ["invoke", "toggle", "select", "expand", "collapse", "set_value", "focus", "click", "double_click", "right_click", "drag", "scroll", "key", "text"]),
            ["batch"] = ("run", ["resume_plan"], ["run"])
        };

    private static readonly HashSet<string> MidiDocumentWrites = new(StringComparer.Ordinal)
    {
        "create", "create_document", "delete", "delete_document", "add_track", "remove_track", "rename_track",
        "create_part", "remove_part", "add_notes", "add_chords", "add_drum_pattern", "add_cc", "add_pitch_bend",
        "add_channel_pressure", "add_poly_pressure", "add_program_change", "add_tempo", "add_time_signature",
        "add_key_signature", "add_marker", "add_lyrics", "add_events", "quantize", "transpose", "humanize",
        "replace_range", "render", "render_file", "export_file", "import_file"
    };

    public static bool IsKnownOperation(string operation) => Families.ContainsKey(operation.Trim().ToLowerInvariant());

    public static NormalizedOperation Normalize(string operation, JsonObject? arguments,
        Func<JsonObject, KeyCommandDescriptor?>? commandLookup = null)
    {
        var family = operation.Trim().ToLowerInvariant();
        if (!Families.TryGetValue(family, out var shape))
            throw new PlanException("UNKNOWN_OPERATION", $"Unknown broker operation '{operation}'.");
        var args = arguments?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var key in CanonicalJson.ControlFields) args.Remove(key);
        var action = args["action"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (family == "batch") action = args["resume_operation_id"] is not null && args["operations"] is null ? "resume_plan" : "run";
        if (string.IsNullOrEmpty(action)) action = shape.Default;
        if (string.IsNullOrEmpty(action))
            throw new PlanException(ErrorCodes.InvalidAction, $"'{family}' requires an explicit action.");
        if (family != "batch") args["action"] = action;
        RejectLegacyTargets(family, args);

        if (family == "midi.compose")
        {
            if (action == "import_to_cubase")
                return Build(family, action, args, OperationScope.Host,
                    args["stop_transport"]?.GetValue<bool>() == true ? RiskLevel.Confirm : RiskLevel.Low, true,
                    args["stop_transport"]?.GetValue<bool>() == true
                        ? "Stop Cubase playback/recording, then import newly created MIDI tracks into the open project."
                        : "Import newly created MIDI tracks into the open project.");
            if (shape.Reads.Contains(action))
                return Build(family, action, args, OperationScope.Read, RiskLevel.None, true, "Read MIDI composition data.");
            if (!MidiDocumentWrites.Contains(action))
                throw new PlanException(ErrorCodes.InvalidAction, $"Unsupported MIDI composition action '{action}'.");
            var destructive = action is "delete" or "delete_document" or "remove_track" or "remove_part" or "replace_range"
                || (action is "render" or "render_file" or "export_file" && args["path"] is not null
                    && args["overwrite"]?.GetValue<bool>() == true);
            return Build(family, action, args, OperationScope.Document, destructive ? RiskLevel.Confirm : RiskLevel.Low, true,
                destructive ? "Delete or replace MIDI composition data, or overwrite an existing MIDI file." : "Edit a session MIDI composition document.");
        }

        if (shape.Reads.Contains(action))
            return Build(family, action, args, OperationScope.Read, RiskLevel.None, true, "Read only.");
        if (!shape.Writes.Contains(action))
            throw new PlanException(ErrorCodes.InvalidAction,
                $"'{family}' does not support action '{action}'. Supported: {string.Join(", ", shape.Reads.Concat(shape.Writes))}.");

        var (risk, classified, impact) = Classify(family, action, args, commandLookup);
        return Build(family, action, args, OperationScope.Host, risk, classified, impact);
    }

    private static NormalizedOperation Build(string family, string action, JsonObject args, string scope,
        RiskLevel risk, bool classified, string impact) => new(family, action, args, scope, risk, classified, impact);

    private static void RejectLegacyTargets(string family, JsonObject args)
    {
        if (family is "track" or "mixer" && args.ContainsKey("track_id"))
            throw new PlanException(ErrorCodes.LegacyTargetAmbiguous,
                "track_id was a 0-63 mixer bank slot in protocol 1 and is ambiguous. Use target_handle from cubase_track(action=list), target_scope=selected, or bank_index.");
        var selectors = new[] { "target_handle", "bank_index", "target_scope" }.Count(args.ContainsKey);
        if (selectors > 1)
            throw new PlanException(ErrorCodes.TargetScopeMismatch,
                "Supply exactly one of target_handle, bank_index or target_scope; conflicting selectors are never reconciled silently.");
        if (args["target_scope"]?.GetValue<string>() is { } scope && !scope.Equals("selected", StringComparison.Ordinal))
            throw new PlanException(ErrorCodes.InvalidArgument, "target_scope supports only 'selected'.");
    }

    private static (RiskLevel Risk, bool Classified, string Impact) Classify(string family, string action, JsonObject args,
        Func<JsonObject, KeyCommandDescriptor?>? commandLookup)
    {
        switch (family)
        {
            case "command":
                var command = commandLookup?.Invoke(args)
                    ?? throw new PlanException(ErrorCodes.CommandNotFound, "Supply a valid command id or an exact category and name.");
                var classified = CapabilityRegistry.TryClassifyCommand(command, out var commandRisk);
                return classified
                    ? (commandRisk, true, $"Execute the Cubase Key Command {command.Category} > {command.Name}.")
                    : (RiskLevel.Confirm, false,
                        $"Execute the unclassified Cubase Key Command {command.Category} > {command.Name}. Its effect has not been reviewed, so it needs confirmation.");
            case "transport":
                return action switch
                {
                    "record" or "record_start" when args["enabled"]?.GetValue<bool>() != false
                        => (RiskLevel.Confirm, true, "Start recording into the current Cubase project."),
                    "record" or "record_stop" => (RiskLevel.Confirm, true, "Stop the current Cubase recording."),
                    _ => (RiskLevel.Low, true, $"Change transport state ({action}).")
                };
            case "track":
                return action switch
                {
                    "delete" => (RiskLevel.Confirm, true, "Delete the planned Cubase track. This removes its events and automation."),
                    "freeze" => (RiskLevel.Confirm, true, "Freeze or unfreeze the planned track (renders or discards a freeze file)."),
                    _ => (RiskLevel.Low, true, $"Track {action}.")
                };
            case "mixer":
            case "direct":
                return (RiskLevel.Low, true, "Change one parameter on the resolved target.");
            case "plugin":
                return action == "assign"
                    ? (RiskLevel.Confirm, true, "Assign a plug-in to the planned slot. Any plug-in already in that slot is discarded with its settings.")
                    : (RiskLevel.Low, true, "Change one plug-in parameter on the resolved slot.");
            case "project":
                return action switch
                {
                    "save_as" when args["overwrite"]?.GetValue<bool>() != true
                        => (RiskLevel.Low, true, "Save the open project under a new file path (no overwrite)."),
                    "save_as" => (RiskLevel.Confirm, true, "Save the open project over an existing file."),
                    "export_audio" when args["overwrite"]?.GetValue<bool>() != true
                        => (RiskLevel.Low, true, "Render a new WAV mixdown file (no overwrite)."),
                    "export_audio" => (RiskLevel.Confirm, true, "Render a WAV mixdown over an existing file."),
                    "save" => (RiskLevel.Confirm, true, "Overwrite the open project's file on disk."),
                    "open" => (RiskLevel.Confirm, true, "Open another project; the current project may be closed or deactivated."),
                    _ => (RiskLevel.Confirm, true, $"Project {action}; unsaved changes may be discarded.")
                };
            case "ui.invoke":
                return action == "focus"
                    ? (RiskLevel.Low, true, "Focus a Cubase-owned UI element.")
                    : (RiskLevel.Confirm, true, "Send one UI input to a Cubase-owned window.");
            case "batch":
                return (RiskLevel.Low, true, "Run a planned batch.");
            default:
                throw new PlanException(ErrorCodes.InvalidAction, $"No risk rule for '{family}.{action}'.");
        }
    }
}
