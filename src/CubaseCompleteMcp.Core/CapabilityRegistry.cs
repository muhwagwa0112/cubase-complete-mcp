namespace CubaseCompleteMcp.Core;

public static class CapabilityRegistry
{
    private static readonly string[] DestructiveNameFragments =
    [
        "delete", "remove", "erase", "empty trash", "discard", "revert", "clear all",
        "remove unused", "remove missing", "flatten", "replace all"
    ];

    public static CommandCapabilityDescriptor DescribeCommand(KeyCommandDescriptor command, bool api13Available)
    {
        var preconditions = api13Available
            ? ["CompleteBridge active", "Matching Cubase context"]
            : new[] { "CompleteBridge active", "Matching Cubase context", "Execution context cannot be preflighted on Cubase before 15.0.20" };
        return new CommandCapabilityDescriptor(command.Id, command.Category, command.Name, command.Index,
            "midi_remote.command_binding", ClassifyCommand(command), "command_backed", preconditions,
            api13Available, false);
    }

    public static RiskLevel ClassifyCommand(KeyCommandDescriptor command)
    {
        var category = command.Category.Trim();
        var name = command.Name.Trim();
        if (DestructiveNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return RiskLevel.Confirm;
        if (category.Equals("File", StringComparison.OrdinalIgnoreCase)
            && (name.Equals("Close", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Quit", StringComparison.OrdinalIgnoreCase)
                || name.Equals("New", StringComparison.OrdinalIgnoreCase)
                || name.Equals("New Empty Project", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Save", StringComparison.OrdinalIgnoreCase)))
            return RiskLevel.Confirm;
        if (category.Equals("Transport", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Record", StringComparison.OrdinalIgnoreCase))
            return RiskLevel.Confirm;
        return RiskLevel.Low;
    }

    public static DirectRootDescriptor[] DirectRoots() =>
    [
        new("selected_track", "Selected track channel, inserts, strip, EQ, sends and instrument", "dynamic", ["Project open", "Track selected", "MIDI Remote API v1.2+"]),
        new("mixer", "MixConsole bank and channel object tree", "dynamic", ["Project open", "MIDI Remote API v1.2+"]),
        new("control_room", "Control Room channels and parameters", "dynamic", ["Control Room configured", "MIDI Remote API v1.2+"]),
        new("focused_quick_controls", "Focused Quick Controls", "dynamic", ["Quick Controls target focused", "MIDI Remote API v1.2+"])
    ];

    public static UiActionDescriptor[] UiActions() =>
    [
        new("invoke", "uia_element", RiskLevel.Confirm, "uia", ["element_token"], "Reinspect after action"),
        new("toggle", "uia_element", RiskLevel.Confirm, "uia", ["element_token"], "Reinspect after action"),
        new("select", "uia_element", RiskLevel.Confirm, "uia", ["element_token"], "Reinspect after action"),
        new("expand", "uia_element", RiskLevel.Confirm, "uia", ["element_token"], "Reinspect after action"),
        new("collapse", "uia_element", RiskLevel.Confirm, "uia", ["element_token"], "Reinspect after action"),
        new("set_value", "uia_element", RiskLevel.Confirm, "uia", ["element_token", "value"], "Reinspect and read value"),
        new("focus", "uia_element", RiskLevel.Low, "uia", ["element_token"], "Foreground owner and focus check"),
        new("click", "snapshot_coordinate", RiskLevel.Confirm, "guarded_send_input", ["snapshot_id", "x", "y"], "Reinspect after action"),
        new("double_click", "snapshot_coordinate", RiskLevel.Confirm, "guarded_send_input", ["snapshot_id", "x", "y"], "Reinspect after action"),
        new("right_click", "snapshot_coordinate", RiskLevel.Confirm, "guarded_send_input", ["snapshot_id", "x", "y"], "Reinspect after action"),
        new("drag", "snapshot_coordinate", RiskLevel.Confirm, "guarded_send_input", ["snapshot_id", "x", "y", "x2", "y2"], "Reinspect after action"),
        new("scroll", "snapshot_coordinate", RiskLevel.Confirm, "guarded_send_input", ["snapshot_id", "x", "y", "delta"], "Reinspect after action"),
        new("key", "snapshot_window", RiskLevel.Confirm, "guarded_send_input", ["snapshot_id", "key"], "Reinspect after action"),
        new("text", "snapshot_window", RiskLevel.Confirm, "guarded_send_input", ["snapshot_id", "value"], "Reinspect and locate entered text")
    ];

    public static MidiCompositionActionDescriptor[] MidiCompositionActions() =>
    [
        new("create_document", "document", RiskLevel.Low, "full", [], "Create a PPQ composition with tempo and time-signature defaults"),
        new("list_documents", "document", RiskLevel.None, "full", [], "List documents owned by this MCP client session"),
        new("get_document", "document", RiskLevel.None, "full", ["document_id"], "Read the complete structured composition"),
        new("delete_document", "document", RiskLevel.Confirm, "full", ["document_id"], "Delete a session composition document"),
        new("add_track", "track", RiskLevel.Low, "full", ["document_id"], "Add a MIDI track and channel"),
        new("remove_track", "track", RiskLevel.Confirm, "full", ["document_id", "track_id"], "Remove a track and its events"),
        new("rename_track", "track", RiskLevel.Low, "full", ["document_id", "track_id", "name"], "Rename a MIDI track"),
        new("create_part", "part", RiskLevel.Low, "full", ["document_id", "track_id"], "Create a named MIDI part range"),
        new("remove_part", "part", RiskLevel.Confirm, "full", ["document_id", "track_id", "part_id"], "Remove a part and its events"),
        new("add_notes", "event", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add note, duration, velocity and release-velocity events"),
        new("add_chords", "generator", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Expand pitch arrays into simultaneous notes"),
        new("add_drum_pattern", "generator", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Expand drum lanes and steps into channel-10 notes"),
        new("add_cc", "event", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add MIDI control-change automation"),
        new("add_pitch_bend", "event", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add 14-bit pitch-bend events"),
        new("add_channel_pressure", "event", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add channel pressure"),
        new("add_poly_pressure", "event", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add polyphonic key pressure"),
        new("add_program_change", "event", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add program-change events"),
        new("add_tempo", "map", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add tempo-map events"),
        new("add_time_signature", "map", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add time-signature events"),
        new("add_key_signature", "map", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add key-signature events"),
        new("add_marker", "text", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add standard MIDI markers"),
        new("add_lyrics", "text", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add standard MIDI lyrics"),
        new("add_events", "event", RiskLevel.Low, "full", ["document_id", "track_id", "events"], "Add any supported channel, meta or SysEx event"),
        new("quantize", "transform", RiskLevel.Low, "full", ["document_id", "track_id", "grid_ticks"], "Quantize timing/duration with strength and swing"),
        new("transpose", "transform", RiskLevel.Low, "full", ["document_id", "track_id", "semitones"], "Transpose note and poly-pressure pitches"),
        new("humanize", "transform", RiskLevel.Low, "full", ["document_id", "track_id"], "Deterministically humanize timing and velocity"),
        new("replace_range", "transform", RiskLevel.Confirm, "full", ["document_id", "track_id", "start_tick", "end_tick"], "Replace all events in a target range"),
        new("render_file", "file", RiskLevel.Low, "full", ["document_id"], "Render a deterministic Standard MIDI File"),
        new("export_file", "file", RiskLevel.Low, "full", ["document_id", "path"], "Export a composition to an explicit Standard MIDI File path"),
        new("inspect_file", "file", RiskLevel.None, "full", ["path"], "Decode and inspect a Standard MIDI File"),
        new("import_file", "file", RiskLevel.Low, "full", ["path"], "Decode a Standard MIDI File into a session document"),
        new("import_to_cubase", "cubase", RiskLevel.Low, "command_and_ui", ["document_id"], "Render and submit a MIDI file through Cubase Import MIDI File")
    ];
}
