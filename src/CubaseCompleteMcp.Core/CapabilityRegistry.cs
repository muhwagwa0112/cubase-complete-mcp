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
}
