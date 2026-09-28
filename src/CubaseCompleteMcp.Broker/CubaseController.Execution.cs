using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

public sealed partial class CubaseController
{
    private Task<BrokerResponse> ExecuteHostAsync(ExecutionContext context, CancellationToken cancellationToken) =>
        context.Operation.Family switch
        {
            "transport" => TransportWriteAsync(context, cancellationToken),
            "mixer" => ParameterWriteAsync(context, "mixer.set", cancellationToken),
            "direct" => ParameterWriteAsync(context, "direct.set", cancellationToken),
            "plugin" when context.Operation.Action == "set_parameter" => ParameterWriteAsync(context, "plugin.set", cancellationToken),
            "plugin" => PluginAssignAsync(context, cancellationToken),
            "track" => TrackWriteAsync(context, cancellationToken),
            "command" => CommandExecuteAsync(context, cancellationToken),
            "project" => ProjectWorkflowAsync(context, cancellationToken),
            "midi.compose" => MidiImportAsync(context, cancellationToken),
            "ui.invoke" => UiInvokeAsync(context),
            _ => Task.FromResult(NotStarted(context.Request, ErrorCodes.InvalidAction, $"No executor for '{context.Operation.Key}'."))
        };

    private static BrokerResponse ApprovalRace(ExecutionContext context) => NotStarted(context.Request,
        ErrorCodes.ConfirmationUnavailable, "The approval was already used by another request; nothing was sent.");

    /// <summary>
    /// Sends one driver write, then observes. The write is never repeated: on timeout only the observation stops.
    /// A no-op receipt (target already satisfied) is still confirmed by a fresh read before it is reported Ok.
    /// </summary>
    private async Task<BrokerResponse> DispatchAndObserveAsync(ExecutionContext context, string driverOperation,
        JsonObject driverArguments, Func<JsonNode, Func<CancellationToken, Task<Observation>>> observerFactory,
        ObservationTimings timings, string tolerance, CancellationToken cancellationToken,
        Func<JsonNode, bool>? appliedEvidence = null)
    {
        if (!context.BeforeDispatch()) return ApprovalRace(context);
        var reply = await _driver.SendAsync(driverOperation, driverArguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Ok) return DriverFailure(context.Request, reply);
        var receipt = reply.Data ?? new JsonObject();
        var mutationSent = ReadBool(receipt["mutation_sent"]) ?? true;
        var epoch = reply.Epoch;
        var observe = observerFactory(receipt);
        async Task<Observation> Guarded(CancellationToken token)
        {
            var observation = await observe(token).ConfigureAwait(false);
            return _driver.Epoch != epoch ? observation with { ContextChanged = true, Note = "The driver session changed during observation." } : observation;
        }

        var outcome = await PostconditionVerifier.ObserveAsync(Guarded,
            mutationSent ? timings : timings with { Deadline = TimeSpan.Zero }, cancellationToken).ConfigureAwait(false);
        var desired = receipt["desired"]?.DeepClone() ?? receipt["requested_process_value"]?.DeepClone()
            ?? receipt["requested_display_value"]?.DeepClone();
        var verification = outcome.ToJson(desired, tolerance);
        var data = new JsonObject
        {
            ["receipt"] = receipt.DeepClone(), ["verification"] = verification, ["mutation_sent"] = mutationSent,
            ["target"] = context.Plan.Target?.ToJson(), ["target_handle"] = context.Plan.Handle?.Handle
        };
        var before = receipt["before"]?.DeepClone();
        var after = outcome.Last?.Observed?.DeepClone();
        if (outcome.Verified)
            return BrokerResponse.Success(context.Request.Id, data, "midi_remote+fresh_read", true, before, after,
                sessionRevision: _driver.Revision,
                executionState: mutationSent ? ExecutionState.Applied : ExecutionState.NotApplied);
        if (outcome.ContextChanged)
            return BrokerResponse.OutcomeUnknown(context.Request.Id,
                "The driver session changed after the change was sent, so its effect could not be observed. It was not re-sent.", data, "midi_remote");
        var applied = mutationSent && appliedEvidence?.Invoke(receipt) == true;
        var code = mutationSent ? ErrorCodes.PostconditionTimeout : "NO_OP_NOT_CONFIRMED";
        var message = mutationSent
            ? $"The change was sent once but the requested state was not observed within {timings.Deadline.TotalSeconds:0.#} s. It was not re-sent."
            : "The driver reported the target as already satisfied, but a fresh read did not confirm it. Nothing was sent.";
        return new BrokerResponse(context.Request.Id, ResultStatus.Unverified, data,
            new BrokerError(code, message, null, false),
            new OperationMetadata("midi_remote+fresh_read", false, before, after, [message], _driver.Revision),
            mutationSent ? applied ? ExecutionState.Applied : ExecutionState.Unknown : ExecutionState.NotApplied);
    }

    // ---- transport ---------------------------------------------------------------------------------------

    private Task<BrokerResponse> TransportWriteAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        var args = (JsonObject)context.Operation.Arguments.DeepClone();
        return DispatchAndObserveAsync(context, "transport.set", args, receipt =>
        {
            var key = receipt["state_key"]?.GetValue<string>() ?? "playing";
            var dispatchRevision = ReadLong(receipt["dispatch_revision"]) ?? long.MaxValue;
            var session = ReadLong(receipt["session_id"]);
            var sent = ReadBool(receipt["mutation_sent"]) ?? true;
            var format = receipt["time_format"]?.GetValue<string>();
            return async token =>
            {
                var snapshot = await ReadDriverAsync("transport.get", new JsonObject(), token).ConfigureAwait(false);
                var state = snapshot["state"] as JsonObject ?? new JsonObject();
                var revision = ReadLong(snapshot["state_revision"]?[key]);
                var fresh = revision is not null && (!sent || revision > dispatchRevision);
                var sameSession = session is null || ReadLong(snapshot["session_id"]) == session;
                var observed = new JsonObject
                {
                    ["key"] = key, ["value"] = state[key]?.DeepClone(), ["state_revision"] = revision,
                    ["dispatch_revision"] = dispatchRevision, ["time_format"] = snapshot["time_formats"]?[key]?.DeepClone()
                };
                if (!sameSession) return new Observation(false, observed, "driver.transport.get", ContextChanged: true);
                var satisfied = fresh && context.Operation.Action switch
                {
                    "tempo" => PostconditionVerifier.WithinTempo(ReadDouble(state["tempo"]), ReadDouble(receipt["desired"]) ?? double.NaN),
                    "position" or "left_locator" or "right_locator" =>
                        state[key]?.GetValue<string>() is { } text && receipt["desired"]?.GetValue<string>() is { } desired
                        && snapshot["time_formats"]?[key]?.GetValue<string>() == format
                        && PostconditionVerifier.CanonicalTimeText(text) == PostconditionVerifier.CanonicalTimeText(desired),
                    "stop" => ReadBool(state["playing"]) == false && ReadBool(state["recording"]) == false,
                    _ => ReadBool(state[key]) is { } value && ReadBool(receipt["desired"]) == value
                };
                return new Observation(satisfied, observed, "driver.transport.get",
                    Note: fresh ? null : "No host callback newer than the dispatch has been observed yet.");
            };
        }, _timings.TransportAndMixer, context.Operation.Action == "tempo" ? "abs(bpm) <= 0.01" : "exact", cancellationToken);
    }

    // ---- parameter writes (mixer / direct / plug-in parameter) -----------------------------------------------

    private Task<BrokerResponse> ParameterWriteAsync(ExecutionContext context, string driverOperation, CancellationToken cancellationToken)
    {
        var target = context.Plan.Target!;
        var args = (JsonObject)context.Operation.Arguments.DeepClone();
        foreach (var key in new[] { "target_handle", "bank_index", "target_scope" }) args.Remove(key);
        foreach (var pair in TargetArguments(target)) args[pair.Key] = pair.Value?.DeepClone();
        var selectedPath = target.Kind == TargetKinds.SelectedTrack && driverOperation == "mixer.set";
        return DispatchAndObserveAsync(context, driverOperation, args, receipt =>
        {
            if (selectedPath) return SelectedMirrorObserver(receipt, target);
            var read = TargetArguments(target);
            if (driverOperation == "mixer.set")
            {
                read["parameter_tag"] = receipt["parameter_tag"]?.DeepClone();
                if (receipt["object_id"] is { } objectId) read["object_id"] = objectId.DeepClone();
            }
            else
            {
                read["object_id"] = receipt["object_id"]?.DeepClone() ?? args["object_id"]?.DeepClone();
                read["parameter_tag"] = receipt["parameter_tag"]?.DeepClone() ?? args["parameter_tag"]?.DeepClone();
            }
            var requestedProcess = ReadDouble(receipt["requested_process_value"]);
            var requestedDisplay = receipt["requested_display_value"]?.GetValue<string>();
            var readOperation = driverOperation == "mixer.set" ? "mixer.get" : "direct.get";
            return async token =>
            {
                var data = await ReadDriverAsync(readOperation, (JsonObject)read.DeepClone(), token).ConfigureAwait(false);
                var value = data["read"] as JsonObject ?? data as JsonObject;
                var process = ReadDouble(value?["process_value"]);
                var display = value?["display_value"]?.GetValue<string>();
                var satisfied = requestedProcess is { } desired ? PostconditionVerifier.WithinNormalized(process, desired)
                    : requestedDisplay is not null && string.Equals(display?.Trim(), requestedDisplay.Trim(), StringComparison.Ordinal);
                return new Observation(satisfied, new JsonObject
                {
                    ["process_value"] = process, ["display_value"] = display, ["target"] = data["target"]?.DeepClone(), ["fresh"] = true
                }, $"driver.{readOperation}(target)");
            };
        }, _timings.TransportAndMixer, "abs(process_value) <= 1e-4; display_value exact",
            cancellationToken, receipt => ReadDouble(receipt["after"]) is { } after && ReadDouble(receipt["before"]) is { } before
                && Math.Abs(after - before) > 1e-9);
    }

    private Func<CancellationToken, Task<Observation>> SelectedMirrorObserver(JsonNode receipt, TargetIdentity target)
    {
        var key = receipt["state_key"]?.GetValue<string>() ?? "volume";
        var dispatchRevision = ReadLong(receipt["dispatch_revision"]) ?? long.MaxValue;
        var sent = ReadBool(receipt["mutation_sent"]) ?? true;
        return async token =>
        {
            var data = await ReadDriverAsync("mixer.get", new JsonObject(), token).ConfigureAwait(false);
            var selected = SelectedIdentity(data["selected"]);
            var observed = new JsonObject
            {
                ["key"] = key, ["value"] = data["state"]?[key]?.DeepClone(),
                ["state_revision"] = data["state_revision"]?[key]?.DeepClone(), ["selected"] = selected.ToJson()
            };
            if (!selected.SameObjectAs(target))
                return new Observation(false, observed, "driver.mixer.get(selected_mirror)", ContextChanged: true,
                    Note: "The selected track changed; the selected-track mirror no longer describes the planned target.");
            var revision = ReadLong(data["state_revision"]?[key]);
            var fresh = revision is not null && (!sent || revision > dispatchRevision);
            var value = data["state"]?[key];
            var satisfied = fresh && (receipt["desired"] is JsonValue desired && ReadBool(desired) is { } flag
                ? ReadBool(value) == flag
                : PostconditionVerifier.WithinNormalized(ReadDouble(value), ReadDouble(receipt["desired"]) ?? double.NaN));
            return new Observation(satisfied, observed, "driver.mixer.get(selected_mirror)");
        };
    }

    // ---- tracks and commands -------------------------------------------------------------------------------

    private async Task<BrokerResponse> TrackWriteAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        switch (context.Operation.Action)
        {
            case "next":
            case "previous":
                var before = SelectedIdentity(context.Plan.Observed["selected_before"]);
                return await DispatchAndObserveAsync(context, "track.set", new JsonObject { ["action"] = context.Operation.Action },
                    _ => async token =>
                    {
                        var now = await ReadSelectedAsync(token).ConfigureAwait(false);
                        return new Observation(now.HasStableIdentity && !now.SameObjectAs(before), now.ToJson(), "driver.track.get");
                    }, _timings.TransportAndMixer, "selection identity changed", cancellationToken).ConfigureAwait(false);
            case "create":
                return await TrackCreateAsync(context, cancellationToken).ConfigureAwait(false);
            case "rename":
                return await TrackRenameAsync(context, cancellationToken).ConfigureAwait(false);
            default:
                return await SelectedTrackCommandAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private JsonObject GuardedCommand(ExecutionContext context) => new()
    {
        ["id"] = context.Plan.Command!.Id,
        ["requires_target"] = true,
        ["expected_selected"] = new JsonObject
        {
            ["unique_id"] = context.Plan.Target!.UniqueId, ["runtime_id"] = context.Plan.Target.RuntimeId
        }
    };

    private async Task<BrokerResponse> SelectedTrackCommandAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        var target = context.Plan.Target!;
        var beforeIds = context.Plan.BeforeChannelIds.ToHashSet(StringComparer.Ordinal);
        var action = context.Operation.Action;
        if (action is "freeze" or "enable")
        {
            if (!context.BeforeDispatch()) return ApprovalRace(context);
            var reply = await _driver.SendAsync("command.execute", GuardedCommand(context), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!reply.Ok) return DriverFailure(context.Request, reply);
            return new BrokerResponse(context.Request.Id, ResultStatus.Unverified,
                new JsonObject { ["receipt"] = reply.Data?.DeepClone(), ["target"] = target.ToJson() }, null,
                new OperationMetadata("midi_remote_command(guarded)", false, null, null,
                    [$"The {action} command was sent once to the planned track, but Cubase exposes no read-back for this state."], _driver.Revision),
                ExecutionState.Unknown);
        }
        return await DispatchAndObserveAsync(context, "command.execute", GuardedCommand(context), _ => async token =>
        {
            var channels = await ReadChannelsAsync(token).ConfigureAwait(false);
            var ids = channels.Select(node => node?["unique_id"]?.GetValue<string>() ?? string.Empty).ToArray();
            var satisfied = action == "delete"
                ? !ids.Contains(target.UniqueId, StringComparer.Ordinal)
                : ids.Count(id => !beforeIds.Contains(id)) == 1;
            return new Observation(satisfied, new JsonObject
            {
                ["channel_count"] = ids.Length, ["target_present"] = ids.Contains(target.UniqueId, StringComparer.Ordinal),
                ["new_channel_ids"] = new JsonArray(ids.Where(id => !beforeIds.Contains(id)).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
            }, "driver.track.list");
        }, _timings.TrackAndPlugin, action == "delete" ? "planned unique_id absent from the bank" : "exactly one new channel identity",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<BrokerResponse> CommandExecuteAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        var args = new JsonObject { ["id"] = context.Plan.Command!.Id };
        if (context.Plan.Target is { } target)
        {
            args["requires_target"] = true;
            args["expected_selected"] = new JsonObject { ["unique_id"] = target.UniqueId, ["runtime_id"] = target.RuntimeId };
        }
        if (!context.BeforeDispatch()) return ApprovalRace(context);
        var reply = await _driver.SendAsync("command.execute", args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Ok) return DriverFailure(context.Request, reply);
        return new BrokerResponse(context.Request.Id, ResultStatus.Unverified,
            new JsonObject { ["receipt"] = reply.Data?.DeepClone(), ["command"] = JsonSerializer.SerializeToNode(context.Plan.Command, PipeProtocol.JsonOptions) },
            null, new OperationMetadata("midi_remote.command_binding", false, null, null,
                ["The Key Command was pulsed once. A raw command has no declared postcondition, so its effect is not verified; read the relevant state before continuing."],
                _driver.Revision), ExecutionState.Unknown);
    }

    private async Task<BrokerResponse> UiInvokeAsync(ExecutionContext context)
    {
        // An expired or foreign snapshot is refused before the approval is consumed: an approval never revives
        // stale coordinates or element tokens.
        if (!_ui.IsInputTargetValid(context.Operation.Arguments, context.Session, out var reason))
            return NotStarted(context.Request, "UI_SNAPSHOT_EXPIRED", reason + " Inspect the window again and plan a new input.");
        if (!context.BeforeDispatch()) return ApprovalRace(context);
        var data = _ui.Invoke(context.Operation.Arguments, context.Session);
        await Task.CompletedTask.ConfigureAwait(false);
        return new BrokerResponse(context.Request.Id, ResultStatus.Unverified, data, null,
            new OperationMetadata("ui_automation_or_send_input", false, null, null,
                ["One UI input was sent. Its effect is not verified; call cubase_ui_inspect for a fresh snapshot."], _driver.Revision),
            ExecutionState.Unknown);
    }
}
