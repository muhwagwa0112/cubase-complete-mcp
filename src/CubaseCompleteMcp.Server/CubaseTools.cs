using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// MCP tools (pipe protocol 2). Every result carries status and execution_state: Ok means the declared postcondition
/// was observed; Unverified with execution_state=unknown and OUTCOME_UNKNOWN means the change may have been applied
/// and must not be retried blindly. Changing tools accept operation_id; when it is omitted on a first request the
/// server issues one and returns it. A confirmation commit reuses the operation_id stored with the approved plan.
/// </summary>
[McpServerToolType]
public sealed class CubaseTools(BrokerClient broker)
{
    private const string TargetHelp = " Target a channel with exactly one of target_handle (from cubase_track action=list), bank_index (0-63 MixConsole bank slot, resolved to a handle), or target_scope=\"selected\" (pinned to the track selected at planning time).";

    [McpServerTool(Name = "cubase_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Return Cubase, broker, MIDI driver (contract version, build ID, epoch, API feature flags), loopMIDI and command-catalog availability.")]
    public Task<JsonNode> Status(CancellationToken cancellationToken = default) => SendAsync("status", null, null, null, cancellationToken);

    [McpServerTool(Name = "cubase_capability_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search supported features and the Key Command catalog with support level (implemented, mock_validated, live_validated, unsupported, blocked_environment, best_effort), risk and classification. Unclassified commands have effective risk Confirm.")]
    public Task<JsonNode> CapabilitySearch(string? query = null, string? category = null, int limit = 100,
        CancellationToken cancellationToken = default) =>
        SendAsync("capabilities.search", Args(("query", query), ("category", category), ("limit", limit)), null, null, cancellationToken);

    [McpServerTool(Name = "cubase_command", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Resolve an exact Cubase Key Command and test can_perform (read) or execute it. Execution pulses the command once and is reported Unverified because a raw command has no declared postcondition. Commands acting on the selected track need a target and are refused if the selection changed." + TargetHelp)]
    public Task<JsonNode> Command(string? category = null, string? name = null, string? id = null,
        string action = "can_perform", string? target_handle = null, string? target_scope = null, int? bank_index = null,
        string? operation_id = null, string? confirmation_id = null, CancellationToken cancellationToken = default) =>
        SendAsync("command", Args(("category", category), ("name", name), ("id", id), ("action", action),
            ("target_handle", target_handle), ("target_scope", target_scope), ("bank_index", bank_index)),
            confirmation_id, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_transport", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Read (action=get) or change transport: play, stop, record/record_start/record_stop (confirmation required), cycle/metronome (enabled), tempo (bpm), position/left_locator/right_locator (position_text in the host's primary time_format, e.g. the format reported by action=get; Cubase reports the format only with a time change, so right after a MIDI Remote script reload these are refused with STATE_UNKNOWN until Play/Stop or a click in the ruler). Each change is sent once and verified by a fresh host callback; toggles are refused while the host state is unknown.")]
    public Task<JsonNode> Transport(string action = "get", bool? enabled = null, double? bpm = null,
        string? position_text = null, string? time_format = null, string? operation_id = null,
        string? confirmation_id = null, CancellationToken cancellationToken = default) =>
        SendAsync("transport", Args(("action", action), ("enabled", enabled), ("bpm", bpm), ("position_text", position_text),
            ("time_format", time_format)), confirmation_id, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_track", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("list: channels of the 64-slot MixConsole bank with target handles (bank_index is a slot, not an identity; channels outside the bank are not listed). get: selected track. create (track_type midi|instrument|audio, name), rename (name), delete/duplicate/freeze/enable (the planned track must be selected; the driver re-checks its identity immediately before the command), next/previous. track_id from protocol 1 is rejected with LEGACY_TARGET_AMBIGUOUS." + TargetHelp)]
    public Task<JsonNode> Track(string action = "list", string? target_handle = null, string? target_scope = null,
        int? bank_index = null, string? name = null, string? track_type = null, string? track_id = null,
        string? operation_id = null, string? confirmation_id = null, CancellationToken cancellationToken = default) =>
        SendAsync("track", Args(("action", action), ("target_handle", target_handle), ("target_scope", target_scope),
            ("bank_index", bank_index), ("name", name), ("track_type", track_type), ("track_id", track_id)),
            confirmation_id, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_mixer", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("get or set one channel parameter on the resolved target. Named parameters: volume, pan (normalized 0-1), mute, solo, monitor, record_enable (boolean). Other parameters: parameter_tag (with optional object_id inside the channel). Reads and writes use the same target; writes are verified by a fresh read of that target (tolerance 1e-4). Ambiguous, hidden or edit-locked parameters are refused." + TargetHelp)]
    public Task<JsonNode> Mixer(string action = "get", string? target_handle = null, string? target_scope = null,
        int? bank_index = null, string? parameter = null, int? parameter_tag = null, long? object_id = null,
        JsonElement? value = null, double? process_value = null, double? plain_value = null, string? display_value = null,
        string? track_id = null, string? operation_id = null, CancellationToken cancellationToken = default) =>
        SendAsync("mixer", Args(("action", action), ("target_handle", target_handle), ("target_scope", target_scope),
            ("bank_index", bank_index), ("parameter", parameter), ("parameter_tag", parameter_tag), ("object_id", object_id),
            ("value", value), ("process_value", process_value), ("plain_value", plain_value), ("display_value", display_value),
            ("track_id", track_id)), null, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_plugin", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("list (bounded DirectAccess snapshot: a target narrows it to that channel's subtree; object_id without parameter_tag reads one object's parameters, e.g. a plug-in on Stereo Out; a reply that stopped early names a continuation node_offset or parameter_offset to pass back), inspect_slot (also reports the state of a queued assignment), catalog (installed plug-in UIDs; returns PLUGIN_CATALOG_UNAVAILABLE when the host offers no verifiable list), set_parameter (parameter_tag + value), assign (plugin_uid from the catalog into slot object_id; confirmation required; the driver loads it on its next idle callback and the result is verified only by the loaded plug-in UID). Removing, bypassing and opening editors are not supported." + TargetHelp)]
    public Task<JsonNode> Plugin(string action = "list", string? target_handle = null, string? target_scope = null,
        int? bank_index = null, string root = "selected_track", long? object_id = null, int? parameter_tag = null,
        double? process_value = null, double? plain_value = null, string? display_value = null, string? plugin_uid = null,
        bool? confirm_replace = null, int? max_nodes = null, int? max_depth = null, int? max_parameters = null,
        bool? include_parameters = null, int? max_total_parameters = null, int? node_offset = null, int? parameter_offset = null,
        string? operation_id = null, string? confirmation_id = null, CancellationToken cancellationToken = default) =>
        SendAsync("plugin", Args(("action", action), ("target_handle", target_handle), ("target_scope", target_scope),
            ("bank_index", bank_index), ("root", root), ("object_id", object_id), ("parameter_tag", parameter_tag),
            ("process_value", process_value), ("plain_value", plain_value), ("display_value", display_value),
            ("plugin_uid", plugin_uid), ("confirm_replace", confirm_replace), ("max_nodes", max_nodes), ("max_depth", max_depth),
            ("max_parameters", max_parameters), ("include_parameters", include_parameters), ("max_total_parameters", max_total_parameters),
            ("node_offset", node_offset), ("parameter_offset", parameter_offset)), confirmation_id, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_direct", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("list/get: bounded snapshot of a DirectAccess root (selected_track, mixer, control_room, focused_quick_controls) or of a target channel's subtree, with per-axis truncation and a per-call parameter/time budget (max_total_parameters, default 256); a reply that stopped early names continuation.node_offset to pass back. object_id alone reads that object's parameters (paged by parameter_offset); target + object_id + parameter_tag is a fresh single-parameter read. set: write one parameter of an object inside the resolved target (object IDs are ephemeral and must belong to that target)." + TargetHelp)]
    public Task<JsonNode> Direct(string action = "list", string root = "selected_track", string? target_handle = null,
        string? target_scope = null, int? bank_index = null, long? object_id = null, int? parameter_tag = null,
        double? process_value = null, double? plain_value = null, string? display_value = null, int? max_nodes = null,
        int? max_depth = null, int? max_parameters = null, bool? include_parameters = null, int? max_total_parameters = null,
        int? node_offset = null, int? parameter_offset = null, string? operation_id = null,
        CancellationToken cancellationToken = default) =>
        SendAsync("direct", Args(("action", action), ("root", root), ("target_handle", target_handle),
            ("target_scope", target_scope), ("bank_index", bank_index), ("object_id", object_id), ("parameter_tag", parameter_tag),
            ("process_value", process_value), ("plain_value", plain_value), ("display_value", display_value),
            ("max_nodes", max_nodes), ("max_depth", max_depth), ("max_parameters", max_parameters),
            ("include_parameters", include_parameters), ("max_total_parameters", max_total_parameters),
            ("node_offset", node_offset), ("parameter_offset", parameter_offset)), null, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_midi", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Session-scoped MIDI compositions (tracks, parts, notes, chords, drums, CC, pitch bend, pressure, program/tempo/signature/text events, transforms, SMF import/export). compare_file compares a document with a MIDI file exported from Cubase (content_roundtrip_verified). import_to_cubase imports new tracks named '<name> [CBMCP-xxxxxxxx]' so pre-existing tracks can never be mistaken for the result; the best level observable through MIDI Remote is targets_observed, reported Unverified with IMPORT_CAUSALITY_UNPROVEN until content is compared. stop_transport=true is required (with confirmation) when Cubase is playing or recording.")]
    public Task<JsonNode> Midi(string action = "list_documents", string? document_id = null,
        string? track_id = null, string? part_id = null, string? name = null, string? path = null,
        bool? stop_transport = null, bool? accept_import_options = null, string? operation_id = null,
        string? confirmation_id = null, JsonElement? options = null, CancellationToken cancellationToken = default) =>
        SendAsync("midi.compose", Merge(options, ("action", action), ("document_id", document_id),
            ("track_id", track_id), ("part_id", part_id), ("name", name), ("path", path),
            ("stop_transport", stop_transport), ("accept_import_options", accept_import_options)),
            confirmation_id, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_project", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("get (project identity), open (existing absolute .cpr; confirmation), save (path of the open project's .cpr; confirmation; verified by the file changing), save_as (absolute .cpr; overwrite=false by default; overwrite=true binds the existing file's size/time/hash into the approval), export_audio (absolute .wav of the current locator range; expected_duration_seconds required; verified as PCM stereo 48 kHz 24-bit within max(20 ms, host_latency_seconds)), new/close (confirmation; not verified). Paths are passed to Cubase's own file dialogs; .cpr files are never edited directly.")]
    public Task<JsonNode> Project(string action = "get", string? path = null, bool? overwrite = null,
        double? expected_duration_seconds = null, double? host_latency_seconds = null, string? operation_id = null,
        string? confirmation_id = null, CancellationToken cancellationToken = default) =>
        SendAsync("project", Args(("action", action), ("path", path), ("overwrite", overwrite),
            ("expected_duration_seconds", expected_duration_seconds), ("host_latency_seconds", host_latency_seconds)),
            confirmation_id, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_ui_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect UI Automation elements and capture a Cubase-owned window. Element tokens expire after 30 seconds and one action. Inspection describes the current UI only; it does not verify an earlier action.")]
    public Task<JsonNode> UiInspect(string? window_title = null, int max_depth = 5, int max_elements = 500,
        bool include_screenshot = true, CancellationToken cancellationToken = default) =>
        SendAsync("ui.inspect", Args(("window_title", window_title), ("max_depth", max_depth),
            ("max_elements", max_elements), ("include_screenshot", include_screenshot)), null, null, cancellationToken);

    [McpServerTool(Name = "cubase_ui_invoke", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Invoke one snapshot-scoped Cubase UI input (confirmation required except focus). The result is always Unverified with execution_state=unknown; call cubase_ui_inspect with a fresh snapshot to check the effect.")]
    public Task<JsonNode> UiInvoke(string action, string? element_token = null, string? snapshot_id = null,
        double? x = null, double? y = null, double? x2 = null, double? y2 = null, int? delta = null,
        string? key = null, string? value = null, string? operation_id = null, string? confirmation_id = null,
        CancellationToken cancellationToken = default) =>
        SendAsync("ui.invoke", Args(("action", action), ("element_token", element_token), ("snapshot_id", snapshot_id),
            ("x", x), ("y", y), ("x2", x2), ("y2", y2), ("delta", delta), ("key", key), ("value", value)),
            confirmation_id, operation_id, cancellationToken);

    [McpServerTool(Name = "cubase_batch", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Plan and run up to 32 operations within timeout_ms (max 180000). Each item: {id, operation, arguments, depends_on}; depends_on defaults to the previous item, [] marks an independent item. Every item is planned and risk-classified first; if any needs confirmation, nothing runs until the whole plan is confirmed. The batch stops at the first failure or unknown outcome; after a known applied-but-unverified item it stops unless continue_on_unverified=true, which only lets independent items run. Completed items are never re-run for the same operation_id. resume_operation_id returns a read-only plan of the remaining items (automatic resume is not supported).")]
    public Task<JsonNode> Batch(JsonElement? operations = null, string? operation_id = null, string? confirmation_id = null,
        bool continue_on_unverified = false, int timeout_ms = BatchPlanner.MaximumTimeoutMs, string? resume_operation_id = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new JsonObject();
        if (resume_operation_id is not null)
        {
            arguments["resume_operation_id"] = resume_operation_id;
            return SendAsync("batch", arguments, null, null, cancellationToken);
        }
        if (operations is not { ValueKind: JsonValueKind.Array }) throw new ArgumentException("operations must be a JSON array.");
        arguments["operations"] = JsonNode.Parse(operations.Value.GetRawText());
        arguments["continue_on_unverified"] = continue_on_unverified;
        arguments["timeout_ms"] = timeout_ms;
        return SendAsync("batch", arguments, confirmation_id, operation_id, cancellationToken);
    }

    [McpServerTool(Name = "cubase_diagnostics", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("action=status: Cubase process/version, loopMIDI ports, driver epoch/contract/build, stale frames discarded, queue and handle counts. action=operation_status: the recorded journal entry of an operation_id owned by this client session.")]
    public Task<JsonNode> Diagnostics(string action = "status", string? operation_id = null, CancellationToken cancellationToken = default) =>
        SendAsync("diagnostics", Args(("action", action), ("operation_id", operation_id)), null, null, cancellationToken);

    private async Task<JsonNode> SendAsync(string operation, JsonObject? arguments, string? confirmationId,
        string? operationId, CancellationToken cancellationToken)
    {
        var request = new BrokerRequest(Guid.NewGuid().ToString("N"), operation, arguments, confirmationId,
            OperationId: operationId ?? (confirmationId is null && !IsRead(operation, arguments) ? Guid.NewGuid().ToString() : null));
        var response = await broker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToNode(response, PipeProtocol.JsonOptions)!;
    }

    private static bool IsRead(string operation, JsonObject? arguments) =>
        OperationReplayPolicy.IsProvablyReadOnly(new BrokerRequest("x", operation, arguments));

    private static JsonObject Args(params (string Name, object? Value)[] fields)
    {
        var result = new JsonObject();
        foreach (var (name, value) in fields)
        {
            if (value is null) continue;
            result[name] = value is JsonElement element ? JsonNode.Parse(element.GetRawText())
                : JsonSerializer.SerializeToNode(value, PipeProtocol.JsonOptions);
        }
        return result;
    }

    private static JsonObject Merge(JsonElement? options, params (string Name, object? Value)[] fields)
    {
        var result = options is { ValueKind: JsonValueKind.Object }
            ? JsonNode.Parse(options.Value.GetRawText())!.AsObject()
            : new JsonObject();
        foreach (var pair in Args(fields).ToArray())
            result[pair.Key] = pair.Value?.DeepClone();
        return result;
    }
}
