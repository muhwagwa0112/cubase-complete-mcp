using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

[McpServerToolType]
public sealed class CubaseTools(BrokerClient broker)
{
    [McpServerTool(Name = "cubase_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Return Cubase, broker, MIDI driver, loopMIDI, project API and command-catalog availability.")]
    public Task<JsonNode> Status(CancellationToken cancellationToken = default) => SendAsync("status", null, null, cancellationToken);

    [McpServerTool(Name = "cubase_capability_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search supported features, all extracted Cubase Key Commands, execution paths, risks and prerequisites.")]
    public Task<JsonNode> CapabilitySearch(string? query = null, string? category = null, int limit = 100,
        CancellationToken cancellationToken = default) =>
        SendAsync("capabilities.search", Args(("query", query), ("category", category), ("limit", limit)), null, cancellationToken);

    [McpServerTool(Name = "cubase_command", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Resolve an exact Cubase Key Command, test canPerform, or execute it. Prefer can_perform before execute.")]
    public Task<JsonNode> Command(string? category = null, string? name = null, string? id = null,
        string action = "can_perform", string? confirmation_id = null, CancellationToken cancellationToken = default) =>
        SendAsync("command", Args(("category", category), ("name", name), ("id", id), ("action", action)), confirmation_id, cancellationToken);

    [McpServerTool(Name = "cubase_transport", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Read or control playback, record, position, locators, cycle, metronome and tempo.")]
    public Task<JsonNode> Transport(string action = "get", bool? enabled = null, double? value = null,
        double? bpm = null, string? confirmation_id = null, JsonElement? options = null,
        CancellationToken cancellationToken = default) =>
        SendAsync("transport", Merge(options, ("action", action), ("enabled", enabled), ("value", value), ("bpm", bpm)), confirmation_id, cancellationToken);

    [McpServerTool(Name = "cubase_track", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("List, select, create, rename, enable, duplicate, freeze or delete tracks using session-scoped selectors when available.")]
    public Task<JsonNode> Track(string action = "list", string? track_id = null, string? name = null,
        string? track_type = null, string? confirmation_id = null, JsonElement? options = null,
        CancellationToken cancellationToken = default) =>
        SendAsync("track", Merge(options, ("action", action), ("track_id", track_id), ("name", name), ("track_type", track_type)), confirmation_id, cancellationToken);

    [McpServerTool(Name = "cubase_mixer", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Read and edit mixer volume, pan, mute, solo, monitor and record-enable on the selected track, or on the channel addressed by track_id. EQ, filters, sends and automation require object_id with parameter_tag. Meters are not exposed by the driver mirror.")]
    public Task<JsonNode> Mixer(string action = "get", string? track_id = null, string? parameter = null,
        double? value = null, string? display_value = null, JsonElement? options = null,
        CancellationToken cancellationToken = default) =>
        SendAsync("mixer", Merge(options, ("action", action), ("track_id", track_id), ("parameter", parameter),
            ("value", value), ("display_value", display_value)), null, cancellationToken);

    [McpServerTool(Name = "cubase_plugin", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Inspect a plug-in slot and its parameters through a DirectAccess root, assign a plug-in to a slot (MIDI Remote API 1.3, requires confirmation), and read or write plug-in parameter values by parameter_tag. Removing, bypassing and opening plug-in editors are not dispatched by the server.")]
    public Task<JsonNode> Plugin(string action = "list", string root = "selected_track", string? object_id = null,
        string? parameter_tag = null, double? process_value = null, double? plain_value = null, string? display_value = null,
        string? plugin_uid = null, string? confirmation_id = null, JsonElement? options = null,
        CancellationToken cancellationToken = default) =>
        SendAsync("plugin", Merge(options, ("action", action), ("root", root), ("object_id", object_id),
            ("parameter_tag", parameter_tag), ("process_value", process_value), ("plain_value", plain_value), ("display_value", display_value),
            ("plugin_uid", plugin_uid)), confirmation_id, cancellationToken);

    [McpServerTool(Name = "cubase_direct", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Traverse any supported DirectAccess root and read or write exposed parameters by ephemeral object ID and parameter tag.")]
    public Task<JsonNode> Direct(string action = "list", string root = "selected_track", string? object_id = null,
        string? parameter_tag = null, double? process_value = null, double? plain_value = null,
        string? display_value = null, JsonElement? options = null, CancellationToken cancellationToken = default) =>
        SendAsync("direct", Merge(options, ("action", action), ("root", root), ("object_id", object_id),
            ("parameter_tag", parameter_tag), ("process_value", process_value), ("plain_value", plain_value),
            ("display_value", display_value)), null, cancellationToken);

    [McpServerTool(Name = "cubase_midi", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Create and edit session-scoped MIDI compositions with tracks, parts, notes, chords, drums, CC, pitch bend, pressure, program/tempo/signature/text events, transforms, SMF import/export, and Cubase import.")]
    public Task<JsonNode> Midi(string action = "list_documents", string? document_id = null,
        string? track_id = null, string? part_id = null, string? name = null, string? path = null,
        string? confirmation_id = null, JsonElement? options = null, CancellationToken cancellationToken = default) =>
        SendAsync("midi.compose", Merge(options, ("action", action), ("document_id", document_id),
            ("track_id", track_id), ("part_id", part_id), ("name", name), ("path", path)),
            confirmation_id, cancellationToken);

    [McpServerTool(Name = "cubase_project", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Run project Key Commands for the currently open project: save, save_as, new, open, close, export_audio, import_audio, import_midi and export_midi. The server dispatches the command binding only; path arguments are not delivered, and actions without a verified binding return PROJECT_REQUIRES_UI instead of a UI fallback.")]
    public Task<JsonNode> Project(string action = "get", string? path = null, bool? overwrite = null,
        string? confirmation_id = null, JsonElement? options = null, CancellationToken cancellationToken = default) =>
        SendAsync("project", Merge(options, ("action", action), ("path", path), ("overwrite", overwrite)), confirmation_id, cancellationToken);

    [McpServerTool(Name = "cubase_ui_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect UI Automation elements and capture a Cubase-owned window. Element tokens expire after 30 seconds and one action.")]
    public Task<JsonNode> UiInspect(string? window_title = null, int max_depth = 5, int max_elements = 500,
        bool include_screenshot = true, CancellationToken cancellationToken = default) =>
        SendAsync("ui.inspect", Args(("window_title", window_title), ("max_depth", max_depth),
            ("max_elements", max_elements), ("include_screenshot", include_screenshot)), null, cancellationToken);

    [McpServerTool(Name = "cubase_ui_invoke", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Invoke one snapshot-scoped Cubase UI input. The result is always Unverified; call cubase_ui_inspect with a fresh snapshot to verify the postcondition.")]
    public Task<JsonNode> UiInvoke(string action, string? element_token = null, string? snapshot_id = null,
        double? x = null, double? y = null, double? x2 = null, double? y2 = null, int? delta = null,
        string? key = null, string? value = null, string? confirmation_id = null,
        CancellationToken cancellationToken = default) =>
        SendAsync("ui.invoke", Args(("action", action), ("element_token", element_token), ("snapshot_id", snapshot_id),
            ("x", x), ("y", y), ("x2", x2), ("y2", y2), ("delta", delta), ("key", key), ("value", value)),
            confirmation_id, cancellationToken);

    [McpServerTool(Name = "cubase_batch", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Run operations sequentially and stop at the first failure. Confirm-required children require an outer confirmation commit.")]
    public Task<JsonNode> Batch(JsonElement operations, string? confirmation_id = null,
        CancellationToken cancellationToken = default)
    {
        if (operations.ValueKind != JsonValueKind.Array) throw new ArgumentException("operations must be a JSON array.");
        return SendAsync("batch", new JsonObject { ["operations"] = JsonNode.Parse(operations.GetRawText()) }, confirmation_id, cancellationToken);
    }

    [McpServerTool(Name = "cubase_diagnostics", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Diagnose Cubase process/version, loopMIDI ports, protocol state, driver events, catalog paths and reconnection errors.")]
    public Task<JsonNode> Diagnostics(CancellationToken cancellationToken = default) => SendAsync("diagnostics", null, null, cancellationToken);

    private async Task<JsonNode> SendAsync(string operation, JsonObject? arguments, string? confirmationId,
        CancellationToken cancellationToken)
    {
        var request = new BrokerRequest(Guid.NewGuid().ToString("N"), operation, arguments, confirmationId);
        var response = await broker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToNode(response, PipeProtocol.JsonOptions)!;
    }

    private static JsonObject Args(params (string Name, object? Value)[] fields)
    {
        var result = new JsonObject();
        foreach (var (name, value) in fields)
            if (value is not null) result[name] = JsonSerializer.SerializeToNode(value, PipeProtocol.JsonOptions);
        return result;
    }

    private static JsonObject Merge(JsonElement? options, params (string Name, object? Value)[] fields)
    {
        var result = options is { ValueKind: JsonValueKind.Object }
            ? JsonNode.Parse(options.Value.GetRawText())!.AsObject()
            : new JsonObject();
        foreach (var (name, value) in fields)
            if (value is not null) result[name] = JsonSerializer.SerializeToNode(value, PipeProtocol.JsonOptions);
        return result;
    }
}
