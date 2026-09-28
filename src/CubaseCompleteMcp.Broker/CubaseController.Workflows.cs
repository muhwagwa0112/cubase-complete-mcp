using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Owned workflows. Each one records its stages, dispatches its triggering command once, drives only dialogs it
/// found for the declared stage (through <see cref="IWorkflowUi"/>, which applies the window ownership policy),
/// and reports the observed postcondition. On failure it reports the last completed stage; it never deletes
/// user files, closes projects, repeats Undo or re-dispatches the command.
/// </summary>
public sealed partial class CubaseController
{
    private sealed class WorkflowLog
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public JsonArray Stages { get; } = [];
        public string LastStage { get; private set; } = "planned";

        public void Add(string stage, JsonObject? evidence = null)
        {
            LastStage = stage;
            Stages.Add(new JsonObject { ["stage"] = stage, ["elapsed_ms"] = _clock.ElapsedMilliseconds, ["evidence"] = evidence });
        }
    }

    private BrokerResponse WorkflowResult(ExecutionContext context, ResultStatus status, string executionState,
        JsonObject data, WorkflowLog log, string path, string? errorCode = null, string? message = null)
    {
        data["workflow_stages"] = log.Stages;
        data["last_stage"] = log.LastStage;
        return new BrokerResponse(context.Request.Id, status, data,
            errorCode is null ? null : new BrokerError(errorCode, message ?? errorCode),
            new OperationMetadata(path, status == ResultStatus.Ok, null, null, message is null ? [] : [message], _driver.Revision),
            executionState);
    }

    private async Task<DriverReply> DispatchCommandOnceAsync(ExecutionContext context, JsonObject commandArguments,
        WorkflowLog log, CancellationToken cancellationToken)
    {
        var reply = await _driver.SendAsync("command.execute", commandArguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        log.Add(reply.Ok ? "command_dispatched" : "command_refused", new JsonObject { ["command"] = context.Plan.Command?.Id, ["error"] = reply.Error?.Code });
        return reply;
    }

    // ---- W1: tracks ----------------------------------------------------------------------------------------

    private async Task<BrokerResponse> TrackCreateAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        var name = context.Operation.Arguments["name"]!.GetValue<string>();
        var type = context.Plan.Targets["track_type"]!.GetValue<string>();
        var log = new WorkflowLog();
        var beforeIds = context.Plan.BeforeChannelIds.ToHashSet(StringComparer.Ordinal);
        if (!context.BeforeDispatch()) return ApprovalRace(context);
        var reply = await DispatchCommandOnceAsync(context, new JsonObject { ["id"] = context.Plan.Command!.Id }, log, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok) return DriverFailure(context.Request, reply);

        var dialog = await _ui.WaitForDialogAsync(DialogKinds.AddTrack, _timings.DialogStage, cancellationToken).ConfigureAwait(false);
        if (dialog is not null)
        {
            log.Add("add_track_dialog_found", new JsonObject { ["title"] = dialog.Title });
            var filled = await _ui.FillAddTrackDialogAsync(dialog, name, cancellationToken).ConfigureAwait(false);
            log.Add(filled.Ok ? "add_track_dialog_submitted" : "add_track_dialog_failed", filled.Evidence);
            if (!filled.Ok)
                return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, new JsonObject(), log,
                    "command+ui", filled.ErrorCode, filled.Message + " The owned Add Track dialog was left open for the user; nothing else was pressed.");
        }
        else
        {
            log.Add("no_add_track_dialog");
        }

        JsonNode? created = null;
        var outcome = await PostconditionVerifier.ObserveAsync(async token =>
        {
            var channels = await ReadChannelsAsync(token).ConfigureAwait(false);
            var added = channels.Where(node => !beforeIds.Contains(node?["unique_id"]?.GetValue<string>() ?? string.Empty)).ToArray();
            created = added.Length == 1 ? added[0]!.DeepClone() : null;
            return new Observation(added.Length == 1, new JsonObject
            {
                ["new_channels"] = new JsonArray(added.Select(node => node?.DeepClone()).ToArray())
            }, "driver.track.list");
        }, _timings.TrackAndPlugin, cancellationToken).ConfigureAwait(false);
        var data = new JsonObject { ["track_type"] = type, ["requested_name"] = name, ["observation"] = outcome.ToJson(null, "exactly one new channel identity") };
        if (!outcome.Verified)
        {
            var message = type is "marker" or "folder" or "tempo" or "signature" or "arranger" or "chord" or "video"
                ? $"A {type} track is not a mixer channel, so its creation cannot be observed through the mixer bank."
                : "No single new mixer channel was observed after the Add Track command. It was not sent again.";
            return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "command+ui+track_list",
                ErrorCodes.PostconditionTimeout, message);
        }
        log.Add("new_channel_observed", created as JsonObject);
        var identity = ChannelIdentity(created);
        data["created"] = identity.ToJson();
        data["target_handle"] = _targets.Issue(context.Session, CurrentEpochs(), identity).Handle;
        if (string.Equals(identity.Title, name, StringComparison.Ordinal))
        {
            log.Add("name_verified");
            return WorkflowResult(context, ResultStatus.Ok, ExecutionState.Applied, data, log, "command+ui+track_list");
        }

        // Cubase created the track without taking the name from a dialog; rename the new track while it is selected.
        var selected = await ReadSelectedAsync(cancellationToken).ConfigureAwait(false);
        if (!selected.SameObjectAs(identity with { Kind = TargetKinds.SelectedTrack }))
            return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Applied, data, log, "command+ui+track_list",
                ErrorCodes.TargetNotSelected, "The track was created, but it is not the selected track, so it was not renamed.");
        var rename = await RenameSelectedAsync(context, identity, name, log, cancellationToken).ConfigureAwait(false);
        data["rename"] = rename.Evidence;
        return rename.Ok
            ? WorkflowResult(context, ResultStatus.Ok, ExecutionState.Applied, data, log, "command+ui+track_list")
            : WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Applied, data, log, "command+ui+track_list",
                rename.ErrorCode, "The track was created, but its name could not be verified: " + rename.Message);
    }

    private async Task<BrokerResponse> TrackRenameAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        var name = context.Operation.Arguments["name"]!.GetValue<string>();
        var target = context.Plan.Target!;
        var log = new WorkflowLog();
        var channel = (await ReadChannelsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(node => node?["unique_id"]?.GetValue<string>() == target.UniqueId);
        if (channel?["title"]?.GetValue<string>() == name)
        {
            log.Add("already_named");
            return WorkflowResult(context, ResultStatus.Ok, ExecutionState.NotApplied,
                new JsonObject { ["target"] = target.ToJson(), ["title"] = name }, log, "track_list");
        }
        if (!context.BeforeDispatch()) return ApprovalRace(context);
        var result = await RenameSelectedAsync(context, target, name, log, cancellationToken).ConfigureAwait(false);
        var data = new JsonObject { ["target"] = target.ToJson(), ["requested_name"] = name, ["rename"] = result.Evidence };
        if (result.Ok) return WorkflowResult(context, ResultStatus.Ok, ExecutionState.Applied, data, log, "guarded_command+ui+track_list");
        if (result.ErrorCode is ErrorCodes.TargetChanged or "COMMAND_NOT_PERFORMABLE")
            return WorkflowResult(context, ResultStatus.Error, ExecutionState.NotApplied, data, log, "guarded_command",
                result.ErrorCode, result.Message);
        return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "guarded_command+ui",
            result.ErrorCode, result.Message);
    }

    /// <summary>
    /// Rename of the selected track: guarded command (identity compared in the driver callback), selection
    /// re-check before any text is typed, then a fresh read of the channel title.
    /// </summary>
    private async Task<UiStageResult> RenameSelectedAsync(ExecutionContext context, TargetIdentity target, string name,
        WorkflowLog log, CancellationToken cancellationToken)
    {
        var command = RequireCommand("Edit", "Rename First Selected Track");
        var reply = await _driver.SendAsync("command.execute", new JsonObject
        {
            ["id"] = command.Id, ["requires_target"] = true,
            ["expected_selected"] = new JsonObject { ["unique_id"] = target.UniqueId, ["runtime_id"] = target.RuntimeId }
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        log.Add(reply.Ok ? "rename_command_dispatched" : "rename_command_refused", new JsonObject { ["error"] = reply.Error?.Code });
        if (!reply.Ok) return UiStageResult.Failure("rename_command", reply.Error?.Code ?? "DRIVER_ERROR", reply.Error?.Message ?? "The rename command was refused.");
        var selected = await ReadSelectedAsync(cancellationToken).ConfigureAwait(false);
        if (selected.UniqueId != target.UniqueId)
        {
            log.Add("selection_changed_before_typing");
            return UiStageResult.Failure("rename_text", ErrorCodes.TargetChanged,
                "The selection changed before the name was typed; no text was entered.");
        }
        var typed = await _ui.TypeRenameTextAsync(name, cancellationToken).ConfigureAwait(false);
        log.Add(typed.Ok ? "rename_text_entered" : "rename_text_failed", typed.Evidence);
        if (!typed.Ok) return typed;
        var outcome = await PostconditionVerifier.ObserveAsync(async token =>
        {
            var channel = (await ReadChannelsAsync(token).ConfigureAwait(false))
                .FirstOrDefault(node => node?["unique_id"]?.GetValue<string>() == target.UniqueId);
            var title = channel?["title"]?.GetValue<string>();
            return new Observation(string.Equals(title, name, StringComparison.Ordinal),
                new JsonObject { ["title"] = title, ["unique_id"] = target.UniqueId }, "driver.track.list");
        }, _timings.TrackAndPlugin, cancellationToken).ConfigureAwait(false);
        log.Add(outcome.Verified ? "name_verified" : "name_not_observed", outcome.ToJson(name, "exact title"));
        return outcome.Verified
            ? UiStageResult.Success("rename", outcome.ToJson(name, "exact title"))
            : UiStageResult.Failure("rename", ErrorCodes.PostconditionTimeout, "The new title was not observed on the planned track.", outcome.ToJson(name, "exact title"));
    }

    // ---- W2/W5: project files and audio export --------------------------------------------------------------

    private async Task<BrokerResponse> ProjectWorkflowAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        var action = context.Operation.Action;
        var log = new WorkflowLog();
        if (action is "new" or "close")
        {
            if (!context.BeforeDispatch()) return ApprovalRace(context);
            var reply = await DispatchCommandOnceAsync(context, new JsonObject { ["id"] = context.Plan.Command!.Id }, log, cancellationToken).ConfigureAwait(false);
            if (!reply.Ok) return DriverFailure(context.Request, reply);
            return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, new JsonObject(), log, "command",
                null, $"File > {(action == "new" ? "New" : "Close")} was sent once. Its dialogs are left to the user and the result is not verified.");
        }
        var file = context.Plan.File!;
        if (action is "save_as" or "export_audio" && file.Exists && !WorkflowPathPolicy.IsUnchanged(file))
            return NotStarted(context.Request, ErrorCodes.FileChanged,
                "The approved overwrite target changed after approval (size, time or hash). Nothing was sent.");
        if (action == "save" && !WorkflowPathPolicy.IsUnchanged(file))
            return NotStarted(context.Request, ErrorCodes.FileChanged, "The project file changed on disk after planning. Nothing was sent.");
        var titleBefore = NormalizeProjectTitle(_processes.Inspect().WindowTitle);
        var started = DateTimeOffset.UtcNow;

        if (!context.BeforeDispatch()) return ApprovalRace(context);
        var dispatched = await DispatchCommandOnceAsync(context, new JsonObject { ["id"] = context.Plan.Command!.Id }, log, cancellationToken).ConfigureAwait(false);
        if (!dispatched.Ok) return DriverFailure(context.Request, dispatched);
        var data = new JsonObject { ["path"] = file.Path, ["file_before"] = file.ToJson(), ["project_title_before"] = titleBefore };

        if (action == "save")
        {
            var saved = await ObserveFileAsync(file, started, _timings.Project, requireNewer: true, cancellationToken).ConfigureAwait(false);
            data["file_after"] = saved.Identity?.ToJson();
            log.Add(saved.Ok ? "file_updated" : "file_not_updated");
            return saved.Ok
                ? WorkflowResult(context, ResultStatus.Ok, ExecutionState.Applied, data, log, "command+file_observation")
                : WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "command+file_observation",
                    ErrorCodes.PostconditionTimeout, "Save was sent once, but the project file was not observed to change.");
        }

        var dialogKind = action switch { "open" => DialogKinds.FileOpen, "save_as" => DialogKinds.FileSave, _ => DialogKinds.ExportAudio };
        var dialog = await _ui.WaitForDialogAsync(dialogKind, _timings.DialogStage, cancellationToken).ConfigureAwait(false);
        if (dialog is null)
        {
            log.Add("dialog_not_found", new JsonObject { ["kind"] = dialogKind });
            return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "command+ui",
                ErrorCodes.StageTimeout, $"The {dialogKind} dialog did not appear within {_timings.DialogStage.TotalSeconds:0} s. The command was not sent again.");
        }
        log.Add("dialog_found", new JsonObject { ["kind"] = dialog.Kind, ["title"] = dialog.Title });

        if (action == "export_audio")
        {
            var configure = await _ui.ConfigureAndStartExportAsync(dialog, Path.GetDirectoryName(file.Path)!,
                Path.GetFileNameWithoutExtension(file.Path), cancellationToken).ConfigureAwait(false);
            log.Add(configure.Ok ? "export_started" : "export_configuration_failed", configure.Evidence);
            if (!configure.Ok)
                return WorkflowResult(context, ResultStatus.Error, ExecutionState.NotApplied, data, log, "command+ui",
                    configure.ErrorCode, configure.Message + " The export dialog was left open; no export was started.");
            return await ObserveExportAsync(context, file, started, data, log, cancellationToken).ConfigureAwait(false);
        }

        var submit = await _ui.SubmitPathAsync(dialog, file.Path, cancellationToken).ConfigureAwait(false);
        log.Add(submit.Ok ? "path_submitted" : "path_submission_failed", submit.Evidence);
        if (!submit.Ok)
            return WorkflowResult(context, ResultStatus.Error, ExecutionState.NotApplied, data, log, "command+ui",
                submit.ErrorCode, submit.Message);

        if (action == "save_as")
        {
            var prompt = await _ui.WaitForDialogAsync(DialogKinds.OverwritePrompt, TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            if (prompt is not null)
            {
                var allowed = context.Operation.Arguments["overwrite"]?.GetValue<bool>() == true && file.Exists
                    && WorkflowPathPolicy.IsUnchanged(file);
                var answered = await _ui.AnswerPromptAsync(prompt, allowed, cancellationToken).ConfigureAwait(false);
                log.Add(allowed ? "overwrite_accepted" : "overwrite_declined", answered.Evidence);
                if (!allowed)
                {
                    if (_ui.IsDialogOpen(dialog))
                    {
                        var cancelled = await _ui.CancelDialogAsync(dialog, cancellationToken).ConfigureAwait(false);
                        log.Add(cancelled.Ok ? "own_save_dialog_cancelled" : "own_save_dialog_left_open", cancelled.Evidence);
                    }
                    var code = file.Exists ? ErrorCodes.FileChanged : ErrorCodes.FileExists;
                    return WorkflowResult(context, ResultStatus.Error, ExecutionState.NotApplied, data, log, "command+ui", code,
                        "Cubase reported an existing file at the target path and overwrite was not approved for it; the overwrite was declined.");
                }
            }
            var saved = await ObserveFileAsync(file, started, _timings.Project, requireNewer: file.Exists, cancellationToken).ConfigureAwait(false);
            var titleAfter = NormalizeProjectTitle(_processes.Inspect().WindowTitle);
            data["file_after"] = saved.Identity?.ToJson();
            data["project_title_after"] = titleAfter;
            var titleMatches = titleAfter.Contains(Path.GetFileNameWithoutExtension(file.Path), StringComparison.OrdinalIgnoreCase);
            log.Add(saved.Ok && titleMatches ? "saved_file_observed" : "save_as_not_confirmed");
            return saved.Ok && titleMatches
                ? WorkflowResult(context, ResultStatus.Ok, ExecutionState.Applied, data, log, "command+ui+file_observation")
                : WorkflowResult(context, ResultStatus.Unverified, saved.Ok ? ExecutionState.Applied : ExecutionState.Unknown, data, log,
                    "command+ui+file_observation", ErrorCodes.PostconditionTimeout,
                    "Save As was submitted once, but both the new file and the project title were not observed. The file (if any) was left in place.");
        }

        // open
        var opened = await PostconditionVerifier.ObserveAsync(token =>
        {
            var title = NormalizeProjectTitle(_processes.Inspect().WindowTitle);
            var ok = !string.Equals(title, titleBefore, StringComparison.Ordinal)
                && title.Contains(Path.GetFileNameWithoutExtension(file.Path), StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new Observation(ok, new JsonObject { ["project_title"] = title }, "process.window_title"));
        }, _timings.Project, cancellationToken).ConfigureAwait(false);
        data["observation"] = opened.ToJson(Path.GetFileName(file.Path), "project title changed and names the file");
        var pendingPrompt = await _ui.WaitForDialogAsync(DialogKinds.SaveChangesPrompt, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (pendingPrompt is not null) data["pending_user_dialog"] = pendingPrompt.Title;
        log.Add(opened.Verified ? "project_opened" : "project_open_not_observed");
        return opened.Verified
            ? WorkflowResult(context, ResultStatus.Ok, ExecutionState.Applied, data, log, "command+ui+window_title")
            : WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "command+ui+window_title",
                ErrorCodes.PostconditionTimeout, pendingPrompt is not null
                    ? "Cubase is waiting on a user dialog (for example unsaved changes); the broker does not answer it."
                    : "The opened project was not observed in the Cubase window title.");
    }

    private sealed record FileObservation(bool Ok, FileIdentity? Identity);

    private static async Task<FileObservation> ObserveFileAsync(FileIdentity planned, DateTimeOffset started, ObservationTimings timings,
        bool requireNewer, CancellationToken cancellationToken)
    {
        FileIdentity? last = null;
        long? stableBytes = null;
        var stableSamples = 0;
        var outcome = await PostconditionVerifier.ObserveAsync(token =>
        {
            var current = FileIdentity.Capture(planned.Path, includeHash: false);
            last = current;
            var newer = current.LastWriteUtc is { } written && written >= started.AddSeconds(-2)
                && (!requireNewer || planned.LastWriteUtc is null || written > planned.LastWriteUtc);
            if (current.Exists && current.Bytes > 0 && newer)
            {
                stableSamples = current.Bytes == stableBytes ? stableSamples + 1 : 0;
                stableBytes = current.Bytes;
            }
            return Task.FromResult(new Observation(stableSamples >= 2, current.ToJson(), "file_system"));
        }, timings, cancellationToken).ConfigureAwait(false);
        return new FileObservation(outcome.Verified, last is null ? null : FileIdentity.Capture(planned.Path));
    }

    private async Task<BrokerResponse> ObserveExportAsync(ExecutionContext context, FileIdentity file, DateTimeOffset started,
        JsonObject data, WorkflowLog log, CancellationToken cancellationToken)
    {
        var expected = ReadDouble(context.Operation.Arguments["expected_duration_seconds"])!.Value;
        var latency = ReadDouble(context.Operation.Arguments["host_latency_seconds"]) ?? 0;
        WavValidation? validation = null;
        var outcome = await PostconditionVerifier.ObserveAsync(token =>
        {
            var current = FileIdentity.Capture(file.Path, includeHash: false);
            if (!current.Exists || current.Bytes is null or < 44 || current.LastWriteUtc < started.AddSeconds(-2))
                return Task.FromResult(new Observation(false, current.ToJson(), "file_system"));
            try
            {
                var bytes = File.ReadAllBytes(file.Path);
                validation = WavFileValidator.Validate(bytes, expected, latency);
                // A header can already be complete while data is still streaming; require the size to match the header.
                return Task.FromResult(new Observation(validation.Valid || validation.Error is not ("data_chunk_truncated" or "data_chunk_missing" or "duration_out_of_tolerance"),
                    validation.ToJson(), "wav_header"));
            }
            catch (IOException)
            {
                return Task.FromResult(new Observation(false, current.ToJson(), "file_system", Note: "file is still locked by the exporter"));
            }
        }, _timings.Export, cancellationToken).ConfigureAwait(false);
        data["wav"] = validation?.ToJson();
        data["file_after"] = FileIdentity.Capture(file.Path).ToJson();
        if (!outcome.Verified)
        {
            log.Add("export_not_completed");
            return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "command+ui+wav_header",
                ErrorCodes.PostconditionTimeout,
                "The export did not produce a complete WAV within 120 s. The output path was left untouched and the export was not started again.");
        }
        if (validation is { Valid: true })
        {
            log.Add("wav_verified");
            return WorkflowResult(context, ResultStatus.Ok, ExecutionState.Applied, data, log, "command+ui+wav_header");
        }
        log.Add("wav_rejected", validation?.ToJson());
        return WorkflowResult(context, ResultStatus.Error, ExecutionState.Applied, data, log, "command+ui+wav_header",
            "EXPORT_FORMAT_MISMATCH", $"A WAV was written but it does not match the required profile or length: {validation?.Error}.");
    }

    // ---- W3: plug-in assignment -----------------------------------------------------------------------------

    private async Task<BrokerResponse> PluginAssignAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        var target = context.Plan.Target!;
        var args = TargetArguments(target);
        args["action"] = "assign";
        args["object_id"] = context.Operation.Arguments["object_id"]!.DeepClone();
        args["plugin_uid"] = context.Operation.Arguments["plugin_uid"]!.DeepClone();
        args["confirm_replace"] = context.Operation.Arguments["confirm_replace"]?.GetValue<bool>() == true;
        args["expected_slot_unique_id"] = context.Plan.Preconditions["slot_unique_id"]?.DeepClone();
        var uid = context.Operation.Arguments["plugin_uid"]!.GetValue<string>();
        var slotId = ReadLong(context.Operation.Arguments["object_id"]) ?? 0;
        var log = new WorkflowLog();
        if (!context.BeforeDispatch()) return ApprovalRace(context);
        var reply = await _driver.SendAsync("plugin.set", args, cancellationToken: cancellationToken).ConfigureAwait(false);
        log.Add(reply.Ok ? "assignment_dispatched" : "assignment_refused", new JsonObject { ["error"] = reply.Error?.Code });
        if (!reply.Ok) return DriverFailure(context.Request, reply);
        var data = new JsonObject { ["receipt"] = reply.Data?.DeepClone(), ["requested_plugin_uid"] = uid, ["target"] = target.ToJson() };
        string? observedUid = null;
        var outcome = await PostconditionVerifier.ObserveAsync(async token =>
        {
            observedUid = await _plugins.ObserveSlotPluginUidAsync(target, slotId, token).ConfigureAwait(false);
            return new Observation(observedUid is not null && observedUid == uid, new JsonObject { ["slot_plugin_uid"] = observedUid }, "plugin_catalog_provider");
        }, _timings.TrackAndPlugin, cancellationToken).ConfigureAwait(false);
        data["verification"] = outcome.ToJson(uid, "exact plug-in UID");
        if (outcome.Verified)
        {
            log.Add("plugin_uid_verified");
            return WorkflowResult(context, ResultStatus.Ok, ExecutionState.Applied, data, log, "midi_remote_api13+uid_readback");
        }
        if (observedUid is not null)
        {
            log.Add("different_plugin_loaded");
            return WorkflowResult(context, ResultStatus.Error, ExecutionState.Applied, data, log, "midi_remote_api13+uid_readback",
                "PLUGIN_MISMATCH", $"The slot holds '{observedUid}', not the requested '{uid}'.");
        }
        var accepted = ReadBool(reply.Data?["accepted"]) == true;
        log.Add(accepted ? "host_accepted_uid_unobservable" : "host_rejected_or_unobservable");
        return WorkflowResult(context, accepted ? ResultStatus.Unverified : ResultStatus.Error,
            accepted ? ExecutionState.Unknown : ExecutionState.Rejected, data, log, "midi_remote_api13",
            accepted ? ErrorCodes.PostconditionTimeout : "PLUGIN_ASSIGN_REJECTED",
            accepted ? "The host accepted the request but the loaded plug-in UID could not be read back; a title change is not treated as proof."
                : "The host rejected the plug-in assignment.");
    }

    // ---- W4: causal MIDI import ---------------------------------------------------------------------------------

    private async Task<BrokerResponse> MidiImportAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timings.MidiImportTotal);
        var token = budget.Token;
        var plan = context.Plan;
        var expectation = plan.Import!;
        var log = new WorkflowLog();
        var tagged = MidiImportCausality.TaggedCopy(plan.Document!, expectation);
        var render = _compositions.RenderTransient(tagged, context.Session, expectation.VerificationTag);
        var roundTrip = StandardMidiFile.Read(render.Bytes, tagged.Name);
        if (roundTrip.Tracks.Count != tagged.Tracks.Count || roundTrip.Ppq != tagged.Ppq)
            throw new InvalidDataException("The tagged MIDI render did not preserve its PPQ and track count.");
        var data = new JsonObject
        {
            ["document"] = MidiCompositionService.Summary(plan.Document!),
            ["verification_tag"] = expectation.VerificationTag,
            ["track_mapping"] = plan.Targets["track_mapping"]?.DeepClone(),
            ["conductor_skipped"] = expectation.ConductorSkipped, ["conductor_reason"] = expectation.ConductorReason,
            ["source_digest"] = expectation.SourceDigest,
            ["rendered_path"] = render.Path, ["rendered_sha256"] = Convert.ToHexString(SHA256.HashData(render.Bytes)).ToLowerInvariant(),
            ["verification_level"] = null
        };
        var beforeIds = plan.BeforeChannelIds.ToHashSet(StringComparer.Ordinal);

        if (!context.BeforeDispatch()) return ApprovalRace(context);
        if (plan.Preconditions["transport_playing"]?.GetValue<bool>() == true || plan.Preconditions["transport_recording"]?.GetValue<bool>() == true)
        {
            var stop = await _driver.SendAsync("transport.set", new JsonObject { ["action"] = "stop" }, cancellationToken: token).ConfigureAwait(false);
            log.Add(stop.Ok ? "transport_stop_sent" : "transport_stop_failed", new JsonObject { ["error"] = stop.Error?.Code });
            if (!stop.Ok) return DriverFailure(context.Request, stop);
            var stopped = await PostconditionVerifier.ObserveAsync(async t =>
            {
                var state = (await ReadDriverAsync("transport.get", new JsonObject(), t).ConfigureAwait(false))["state"];
                return new Observation(ReadBool(state?["playing"]) == false && ReadBool(state?["recording"]) == false, state?.DeepClone(), "driver.transport.get");
            }, _timings.TransportAndMixer, token).ConfigureAwait(false);
            if (!stopped.Verified)
                return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.PartiallyApplied, data, log, "midi_remote",
                    ErrorCodes.PostconditionTimeout, "Stop was sent but the transport was not observed stopped; the import was not started.");
        }

        var recovered = await _ui.DismissProjectFolderDialogAsync(token).ConfigureAwait(false);
        if (recovered is not null) log.Add("stale_project_folder_dialog_closed", recovered);
        var reusedDialog = _ui.HasOpenMidiImportFileDialog();
        if (_ui.HasOpenFileDialog() && !reusedDialog)
            return WorkflowResult(context, ResultStatus.Error, ExecutionState.NotStarted, data, log, "ui",
                "FOREIGN_FILE_DIALOG_OPEN", "A non-MIDI Cubase file dialog is already open; the import was not started.");
        if (!reusedDialog)
        {
            var command = await DispatchCommandOnceAsync(context, new JsonObject { ["id"] = plan.Command!.Id }, log, token).ConfigureAwait(false);
            if (!command.Ok) return DriverFailure(context.Request, command);
        }
        var stage = reusedDialog ? "midi_file_dialog" : (await _ui.WaitForMidiImportStageAsync((int)_timings.DialogStage.TotalMilliseconds, token).ConfigureAwait(false))["stage"]?.GetValue<string>();
        log.Add("stage_" + stage);
        var menuFallbacks = 0;
        if (stage == "project_choice")
        {
            log.Add("project_choice_existing", await _ui.ChooseExistingProjectForMidiImportAsync(token).ConfigureAwait(false));
            stage = (await _ui.WaitForMidiImportStageAsync((int)_timings.DialogStage.TotalMilliseconds, token).ConfigureAwait(false))["stage"]?.GetValue<string>();
        }
        if (MidiImportWorkflowPolicy.ShouldUseMenuFallback(stage, menuFallbacks))
        {
            menuFallbacks++;
            log.Add("menu_fallback", await _ui.OpenMidiImportDialogViaMenuAsync(token).ConfigureAwait(false));
            stage = (await _ui.WaitForMidiImportStageAsync((int)_timings.DialogStage.TotalMilliseconds, token).ConfigureAwait(false))["stage"]?.GetValue<string>();
            if (stage == "project_choice")
            {
                log.Add("project_choice_existing", await _ui.ChooseExistingProjectForMidiImportAsync(token).ConfigureAwait(false));
                stage = (await _ui.WaitForMidiImportStageAsync((int)_timings.DialogStage.TotalMilliseconds, token).ConfigureAwait(false))["stage"]?.GetValue<string>();
            }
        }
        data["menu_fallback_count"] = menuFallbacks;
        if (stage != "midi_file_dialog" && !_ui.HasOpenMidiImportFileDialog())
            return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "command+ui",
                ErrorCodes.StageTimeout, "The MIDI file dialog did not appear after the command and one menu fallback. Nothing was submitted.");
        var submitted = await _ui.SubmitFileDialogAsync(render.Path, token).ConfigureAwait(false);
        log.Add("file_submitted", submitted);
        if (submitted["dialog_closed"]?.GetValue<bool>() != true)
            return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "command+ui",
                "MIDI_FILE_DIALOG_REMAINS", "The MIDI file dialog did not close after one submission; it was not submitted again.");
        stage = (await _ui.WaitForMidiImportStageAsync(3000, token).ConfigureAwait(false))["stage"]?.GetValue<string>();
        if (stage == "project_choice")
        {
            log.Add("project_choice_existing", await _ui.ChooseExistingProjectForMidiImportAsync(token).ConfigureAwait(false));
            stage = (await _ui.WaitForMidiImportStageAsync(3000, token).ConfigureAwait(false))["stage"]?.GetValue<string>();
        }
        if (stage == "import_options")
        {
            if (context.Operation.Arguments["accept_import_options"]?.GetValue<bool>() == false)
            {
                log.Add("import_options_pending");
                data["pending_dialog"] = "import_options";
                return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "command+ui",
                    "IMPORT_OPTIONS_PENDING", "Cubase is showing MIDI import options and accept_import_options=false, so they were not accepted.");
            }
            log.Add("import_options_accepted", await _ui.AcceptImportOptionsAsync(token).ConfigureAwait(false));
        }

        var expectedNames = expectation.Tracks.Select(track => track.ImportedName).ToArray();
        JsonArray newChannels = [];
        var observed = await PostconditionVerifier.ObserveAsync(async t =>
        {
            var channels = await ReadChannelsAsync(t).ConfigureAwait(false);
            newChannels = new JsonArray(channels.Where(node => !beforeIds.Contains(node?["unique_id"]?.GetValue<string>() ?? string.Empty))
                .Select(node => node?.DeepClone()).ToArray());
            var titles = newChannels.Select(node => node?["title"]?.GetValue<string>()).ToArray();
            var all = expectedNames.All(name => titles.Count(title => title == name) == 1);
            return new Observation(all, new JsonObject { ["new_channels"] = newChannels.DeepClone() }, "driver.track.list");
        }, _timings.TrackAndPlugin with { Deadline = TimeSpan.FromSeconds(Math.Min(10, _timings.MidiImportTotal.TotalSeconds)) }, token).ConfigureAwait(false);
        var mapping = new JsonArray(expectation.Tracks.Select(track =>
        {
            var created = newChannels.FirstOrDefault(node => node?["title"]?.GetValue<string>() == track.ImportedName);
            var entry = new JsonObject { ["source_index"] = track.SourceIndex, ["original_name"] = track.OriginalName, ["created_name"] = track.ImportedName };
            if (created is not null)
            {
                var identity = ChannelIdentity(created);
                entry["created"] = identity.ToJson();
                entry["target_handle"] = _targets.Issue(context.Session, CurrentEpochs(), identity).Handle;
            }
            return (JsonNode?)entry;
        }).ToArray());
        data["created_tracks"] = mapping;
        data["observation"] = observed.ToJson(new JsonArray(expectedNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()), "exact tagged titles on new identities");
        if (!observed.Verified)
        {
            data["verification_level"] = MidiVerificationLevels.Submitted;
            log.Add("targets_not_observed");
            return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Unknown, data, log, "smf+command+ui+track_list",
                ErrorCodes.ImportCausalityUnproven, "The MIDI file was submitted, but new tracks with the tagged names were not all observed. It was not imported again.");
        }
        data["verification_level"] = MidiVerificationLevels.TargetsObserved;
        log.Add("targets_observed");
        // The MIDI Remote API exposes no event/part read-back, so parts_observed needs owned-fixture evidence
        // (export the new tracks and compare with cubase_midi action=compare_file).
        return WorkflowResult(context, ResultStatus.Unverified, ExecutionState.Applied, data, log, "smf+command+ui+track_list",
            ErrorCodes.ImportCausalityUnproven,
            "New tracks with the unique tagged names were observed. Their parts and events cannot be read through MIDI Remote, so the import is not reported Ok; export them from Cubase and run cubase_midi action=compare_file for content verification.");
    }
}
