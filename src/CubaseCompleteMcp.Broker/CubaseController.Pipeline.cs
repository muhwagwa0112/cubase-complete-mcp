using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

public sealed partial class CubaseController
{
    public async Task<BrokerResponse> HandleAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var compatibility = BrokerCompatibility.Evaluate(request.ClientProtocolVersion,
            request.ClientReleaseId, _buildIdentity);
        if (!compatibility.ProtocolCompatible)
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, ErrorCodes.BrokerProtocolMismatch,
                $"Client pipe protocol {request.ClientProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} is incompatible with broker protocol {CubaseConstants.PipeProtocolVersion}. Nothing was executed.");
        var response = await PipelineAsync(request, cancellationToken).ConfigureAwait(false);
        var warnings = response.Metadata?.Warnings.ToList() ?? [];
        warnings.AddRange(compatibility.Warnings);
        response = response with { Id = request.Id };
        return response.Metadata is null ? response : response with
        {
            Metadata = response.Metadata with { Warnings = warnings }
        };
    }

    private sealed class ExecutionContext
    {
        public required BrokerRequest Request { get; init; }
        public required NormalizedOperation Operation { get; init; }
        public required HostPlan Plan { get; init; }
        public required string Session { get; init; }
        public required string OperationId { get; init; }
        public JournalEntry? Journal { get; set; }
        public bool Dispatched { get; private set; }
        public Func<bool>? OnDispatch { get; init; }

        /// <summary>
        /// Called immediately before the first external change. Consumes the approval and flushes the journal
        /// state 'dispatching'. Returns false when the approval was consumed concurrently; nothing is sent then.
        /// </summary>
        public bool BeforeDispatch()
        {
            if (Dispatched) return true;
            if (OnDispatch is not null && !OnDispatch()) return false;
            Dispatched = true;
            return true;
        }
    }

    private async Task<BrokerResponse> PipelineAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var session = request.ClientSessionId ?? "unscoped";
        NormalizedOperation operation;
        try
        {
            operation = OperationPlanner.Normalize(request.Operation, request.Arguments, CommandLookup);
        }
        catch (PlanException error)
        {
            return NotStarted(request, error.Code, error.Message);
        }

        try
        {
            if (operation.Family == "batch" && operation.Action == "run")
                return await BatchAsync(request, session, cancellationToken).ConfigureAwait(false);
            if (operation.Scope == OperationScope.Read)
                return await ReadAsync(request, operation, session, cancellationToken).ConfigureAwait(false);
            if (operation.Scope == OperationScope.Document)
                return DocumentOperation(request, operation, session);
            return await HostMutationAsync(request, operation, session, null, cancellationToken).ConfigureAwait(false);
        }
        catch (PlanException error)
        {
            return NotStarted(request, error.Code, error.Message);
        }
        catch (OperationCanceledException)
        {
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, ErrorCodes.Cancelled, "The operation was cancelled before dispatch.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or IOException or InvalidDataException or JsonException or FormatException)
        {
            return BrokerResponse.Failure(request.Id, ResultStatus.Error, "OPERATION_FAILED", error.Message);
        }
    }

    private static BrokerResponse NotStarted(BrokerRequest request, string code, string message, JsonNode? details = null) =>
        BrokerResponse.Failure(request.Id, code is "UNKNOWN_OPERATION" or ErrorCodes.PluginCatalogUnavailable
                ? ResultStatus.Unsupported : code == ErrorCodes.DriverOffline ? ResultStatus.Offline : ResultStatus.Error,
            code, message, details) with { OperationId = request.OperationId };

    private static string ResolveOperationId(BrokerRequest request, ConfirmationProposal? proposal)
    {
        var supplied = request.OperationId ?? request.Arguments?["operation_id"]?.GetValue<string>();
        if (proposal?.OperationId is { } stored)
        {
            if (supplied is not null && !supplied.Equals(stored, StringComparison.OrdinalIgnoreCase))
                throw new PlanException(ErrorCodes.ConfirmationMismatch,
                    "operation_id differs from the operation_id stored with the approved plan.");
            return stored;
        }
        if (supplied is null) return Guid.NewGuid().ToString();
        ExecutionJournal.ValidateOperationId(supplied);
        return supplied.ToLowerInvariant();
    }

    /// <summary>
    /// Runs one host mutation. <paramref name="batchChild"/> is set when a confirmed batch runs a child: the batch
    /// already holds the lease and the approval, and the child's targets must still equal the planned ones.
    /// </summary>
    private async Task<BrokerResponse> HostMutationAsync(BrokerRequest request, NormalizedOperation operation,
        string session, BatchChildContext? batchChild, CancellationToken cancellationToken)
    {
        ConfirmationProposal? proposal = null;
        string operationId;
        if (batchChild is not null)
        {
            operationId = batchChild.OperationId;
        }
        else
        {
            // Validate an approval without consuming it first, so the operation_id stored with the plan is used.
            if (!string.IsNullOrWhiteSpace(request.ConfirmationId))
            {
                var check = _confirmations.Validate(request.ConfirmationId, session, operation.Key, operation.Arguments);
                if (!check.Ok) return NotStarted(request, check.ErrorCode!, check.Message!);
                proposal = check.Proposal;
            }
            operationId = ResolveOperationId(request, proposal);
        }
        request = request with { OperationId = operationId };

        if (batchChild is null)
        {
            // Plan outside the lease: waiting for approval must never hold the mutation lease.
            var outerPlan = await BuildPlanAsync(operation, session, operationId, cancellationToken).ConfigureAwait(false);
            var outerHash = outerPlan.Hash(operation);
            if (proposal is null)
            {
                var begin = _journal.Begin(session, operationId, outerHash, operation.Key, outerPlan.TargetsJson(),
                    outerPlan.Risk == RiskLevel.Confirm);
                var early = JournalShortCircuit(request, begin);
                if (early is not null) return early;
                if (outerPlan.Risk == RiskLevel.Confirm)
                {
                    var prepared = _confirmations.Prepare(session, operation.Key, operation.Arguments, outerPlan.Impact,
                        operationId, outerHash, outerPlan.ToJson(operation));
                    return new BrokerResponse(request.Id, ResultStatus.RequiresConfirmation,
                        JsonSerializer.SerializeToNode(prepared, PipeProtocol.JsonOptions), null,
                        new OperationMetadata("safety_policy", false, null, null,
                            ["No change was made. Commit the same request with confirmation_id (operation_id is kept with the plan)."], _driver.Revision),
                        ExecutionState.NotStarted, operationId);
                }
            }
            else if (!string.Equals(proposal.PlanHash, outerHash, StringComparison.Ordinal))
            {
                var current = outerPlan.ToJson(operation);
                return NotStarted(request, PlanMismatchCode(proposal.Plan, current),
                    "The target, file or project conditions changed after approval. Nothing was sent and the approval was kept; request a new plan to see the current conditions.",
                    new JsonObject { ["approved_plan"] = proposal.Plan?.DeepClone(), ["current_plan"] = current });
            }
        }

        return await _coordinator.RunExclusiveAsync(operation.Key, async () =>
        {
            var plan = await BuildPlanAsync(operation, session, operationId, cancellationToken).ConfigureAwait(false);
            var hash = plan.Hash(operation);
            if (batchChild is not null)
            {
                if (batchChild.ApprovedTargetsHash is not null
                    && !string.Equals(batchChild.ApprovedTargetsHash, plan.TargetsHash(), StringComparison.Ordinal))
                    return NotStarted(request, ErrorCodes.TargetChanged,
                        "This batch operation's target changed after the batch was planned. Nothing was sent for it.");
            }
            else if (proposal is not null && !string.Equals(proposal.PlanHash, hash, StringComparison.Ordinal))
            {
                return NotStarted(request, PlanMismatchCode(proposal.Plan, plan.ToJson(operation)),
                    "The target, file or project conditions changed while waiting for the mutation lease. Nothing was sent and the approval was kept.");
            }
            else if (proposal is null && plan.Risk == RiskLevel.Confirm)
            {
                return NotStarted(request, ErrorCodes.TargetChanged,
                    "Cubase state changed so that this operation now needs confirmation (for example recording started). Nothing was sent; request it again.");
            }

            JournalEntry? entry = null;
            if (batchChild is null)
            {
                var begin = _journal.Begin(session, operationId, proposal?.PlanHash ?? hash, operation.Key, plan.TargetsJson(), false);
                var early = JournalShortCircuit(request, begin);
                if (early is not null) return early;
                entry = begin.Entry;
            }

            ExecutionContext? context = null;
            context = new ExecutionContext
            {
                Request = request, Operation = operation, Plan = plan, Session = session, OperationId = operationId,
                Journal = entry,
                OnDispatch = () =>
                {
                    if (proposal is not null && !_confirmations.TryConsume(proposal.ConfirmationId)) return false;
                    if (batchChild is not null && !batchChild.MarkDispatched()) return false;
                    if (context!.Journal is not null) context.Journal = _journal.MarkDispatching(context.Journal);
                    return true;
                }
            };
            BrokerResponse response;
            try
            {
                response = await ExecuteHostAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (PlanException error)
            {
                response = context.Dispatched
                    ? BrokerResponse.OutcomeUnknown(request.Id, error.Message)
                    : BrokerResponse.Failure(request.Id, ResultStatus.Error, error.Code, error.Message);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException
                or InvalidDataException or JsonException or ArgumentException or FormatException or TimeoutException)
            {
                response = context.Dispatched
                    ? BrokerResponse.OutcomeUnknown(request.Id, $"The change was dispatched but its workflow failed before its result was known: {error.Message} ({Origin(error)})")
                    : BrokerResponse.Failure(request.Id, ResultStatus.Error,
                        error is OperationCanceledException ? ErrorCodes.Cancelled : "OPERATION_FAILED", error.Message);
            }
            response = response with { OperationId = operationId };
            if (!context.Dispatched && response.ExecutionState is ExecutionState.Applied or ExecutionState.Unknown
                    or ExecutionState.PartiallyApplied)
                response = response with { ExecutionState = ExecutionState.NotStarted };
            if (context.Journal is not null)
            {
                if (context.Dispatched || response.Status == ResultStatus.Ok) _journal.Complete(context.Journal, response);
                else _journal.DiscardUndispatched(context.Journal);
            }
            return response;
        }, refusal => NotStarted(request, refusal.ErrorCode, refusal.Message), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Exception type and the innermost project frame, so a workflow failure can be located from its result.</summary>
    internal static string Origin(Exception error)
    {
        var frame = (error.StackTrace ?? string.Empty).Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Contains("CubaseCompleteMcp.", StringComparison.Ordinal));
        if (frame is not null)
        {
            var end = frame.IndexOf(" in ", StringComparison.Ordinal);
            if (end > 0) frame = frame[..end];
        }
        return frame is null ? error.GetType().Name : $"{error.GetType().Name} {frame}";
    }

    /// <summary>FILE_CHANGED when only a bound file identity differs; TARGET_CHANGED for any other plan difference.</summary>
    private static string PlanMismatchCode(JsonNode? approved, JsonNode current)
    {
        var approvedFile = approved?["preconditions"]?["file"];
        var currentFile = current["preconditions"]?["file"];
        var fileDiffers = approvedFile is not null && currentFile is not null
            && CanonicalJson.Serialize(approvedFile, false) != CanonicalJson.Serialize(currentFile, false);
        var restSame = CanonicalJson.Serialize(approved?["targets"], false) == CanonicalJson.Serialize(current["targets"], false);
        return fileDiffers && restSame ? ErrorCodes.FileChanged : ErrorCodes.TargetChanged;
    }

    private static BrokerResponse? JournalShortCircuit(BrokerRequest request, JournalBeginResult begin) => begin.Kind switch
    {
        JournalBeginKind.Conflict => NotStarted(request, ErrorCodes.IdempotencyConflict,
            "This operation_id was already used with a different plan. Use a new operation_id for a different request."),
        JournalBeginKind.ReplayCompleted => RecordedResult(request, begin.Entry,
            "Returned the recorded result for this operation_id; nothing was sent again."),
        JournalBeginKind.OutcomeUnknown => BrokerResponse.OutcomeUnknown(request.Id,
            "This operation_id was already dispatched and its outcome is unknown. It is not re-sent; read Cubase state before deciding.",
            new JsonObject { ["journal_state"] = begin.Entry.State, ["recovery_note"] = begin.Entry.RecoveryNote })
            with { OperationId = begin.Entry.OperationId },
        _ => null
    };

    private static BrokerResponse RecordedResult(BrokerRequest request, JournalEntry entry, string warning)
    {
        var status = Enum.TryParse<ResultStatus>(entry.ResultStatus, out var parsed) ? parsed : ResultStatus.Unverified;
        return new BrokerResponse(request.Id, status, new JsonObject
        {
            ["replayed_from_journal"] = true, ["recorded_result"] = entry.Result?.DeepClone(), ["children"] = entry.Children?.DeepClone()
        }, null, new OperationMetadata("execution_journal", status == ResultStatus.Ok, null, null, [warning], 0),
            entry.ExecutionState ?? ExecutionState.Unknown, entry.OperationId);
    }

    private KeyCommandDescriptor? CommandLookup(JsonObject arguments)
    {
        if (_commands is null) return null;
        KeyCommandDescriptor? command = null;
        if (arguments["id"]?.GetValue<string>() is { Length: > 0 } id) command = _commands.FindById(id);
        if (command is null && arguments["category"]?.GetValue<string>() is { Length: > 0 } category
            && arguments["name"]?.GetValue<string>() is { Length: > 0 } name)
            command = _commands.Find(category, name);
        return command;
    }

    private KeyCommandDescriptor RequireCommand(string category, string name) =>
        _commands?.Find(category, name) ?? throw new PlanException("COMMAND_UNAVAILABLE",
            $"The Key Command {category} > {name} is not in the loaded command catalog.");

    // ---- epochs and identities --------------------------------------------------------------------------

    private HostEpochs CurrentEpochs() => new(_brokerInstanceId, _driver.Epoch, ProjectEpoch());

    private string ProjectEpoch()
    {
        var process = _processes.Inspect();
        if (!process.Running) return "cubase_not_running";
        var title = NormalizeProjectTitle(process.WindowTitle);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{process.ProcessId}|{process.StartTime:O}|{title}"))).ToLowerInvariant()[..24];
    }

    internal static string NormalizeProjectTitle(string? title) =>
        (title ?? string.Empty).Replace("*", string.Empty, StringComparison.Ordinal).Trim();

    private static TargetIdentity ChannelIdentity(JsonNode? node) => new(TargetKinds.MixerChannel, "mixer",
        node?["unique_id"]?.GetValue<string>(), ReadLong(node?["runtime_id"]), ReadInt(node?["bank_index"]),
        node?["title"]?.GetValue<string>(), ReadLong(node?["object_id"]));

    private static TargetIdentity SelectedIdentity(JsonNode? node) => new(TargetKinds.SelectedTrack, "selected_track",
        node?["unique_id"]?.GetValue<string>(), ReadLong(node?["runtime_id"]), null, node?["title"]?.GetValue<string>());

    internal static long? ReadLong(JsonNode? node) => JsonNumbers.ReadLong(node);

    internal static int? ReadInt(JsonNode? node) => ReadLong(node) is { } value ? (int)value : null;

    internal static double? ReadDouble(JsonNode? node) => JsonNumbers.ReadDouble(node);

    internal static bool? ReadBool(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<bool>(); }
        catch (Exception error) when (error is FormatException or InvalidOperationException) { return null; }
    }

    private static JsonObject ExpectedJson(TargetIdentity identity) => new()
    {
        ["unique_id"] = identity.UniqueId, ["runtime_id"] = identity.RuntimeId, ["bank_index"] = identity.BankIndex
    };

    private static BrokerResponse DriverFailure(BrokerRequest request, DriverReply reply)
    {
        var code = reply.Error?.Code ?? "DRIVER_ERROR";
        if (code == ErrorCodes.OutcomeUnknown)
            return BrokerResponse.OutcomeUnknown(request.Id, reply.Error?.Message ?? "The driver did not answer after the change was sent.",
                reply.Error?.Details, "midi_remote");
        var status = code == ErrorCodes.DriverOffline ? ResultStatus.Offline
            : code is "UNSUPPORTED_CAPABILITY" or ErrorCodes.PluginCatalogUnavailable ? ResultStatus.Unsupported
            : ResultStatus.Error;
        // Driver-side refusals (STATE_UNKNOWN, TARGET_CHANGED, PARAMETER_LOCKED, ...) are raised before any host call.
        return BrokerResponse.Failure(request.Id, status, code, reply.Error?.Message ?? "Cubase driver operation failed.",
            reply.Error?.Details, "midi_remote", ExecutionState.NotStarted);
    }
}
