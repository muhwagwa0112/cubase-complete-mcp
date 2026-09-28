using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

public sealed partial class CubaseController
{
    /// <summary>Per-child execution context inside a batch that already holds the lease and the approval.</summary>
    private sealed class BatchChildContext(string operationId, string? approvedTargetsHash, Func<bool> onFirstDispatch)
    {
        public string OperationId { get; } = operationId;
        public string? ApprovedTargetsHash { get; } = approvedTargetsHash;
        public bool Dispatched { get; private set; }

        public bool MarkDispatched()
        {
            if (!onFirstDispatch()) return false;
            Dispatched = true;
            return true;
        }
    }

    private static string ChildOperationId(string batchOperationId, string childId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(batchOperationId + "/" + childId))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    private async Task<(JsonObject Json, Dictionary<string, string> TargetHashes, RiskLevel Risk)> PlanBatchChildrenAsync(
        BatchPlan batch, string session, string operationId, CancellationToken cancellationToken)
    {
        var children = new JsonArray();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var risk = batch.Risk;
        foreach (var child in batch.Children)
        {
            var entry = new JsonObject { ["id"] = child.Id, ["operation"] = child.Operation.Key, ["arguments"] = child.Operation.Arguments.DeepClone() };
            if (child.Operation.IsHostMutation)
            {
                HostPlan plan;
                try
                {
                    plan = await BuildPlanAsync(child.Operation, session, ChildOperationId(operationId, child.Id), cancellationToken).ConfigureAwait(false);
                }
                catch (PlanException error)
                {
                    throw new PlanException(error.Code, $"operations[{child.Index}] ({child.Id}): {error.Message}");
                }
                if (plan.Risk == RiskLevel.Confirm) risk = RiskLevel.Confirm;
                hashes[child.Id] = plan.TargetsHash();
                entry["risk"] = plan.Risk.ToString();
                entry["impact"] = plan.Impact;
                entry["targets"] = plan.Targets.DeepClone();
                entry["preconditions"] = plan.Preconditions.DeepClone();
            }
            else
            {
                entry["risk"] = child.Operation.Risk.ToString();
                entry["impact"] = child.Operation.Impact;
            }
            children.Add(entry);
        }
        var json = batch.ToJson();
        json["children"] = children;
        return (json, hashes, risk);
    }

    private async Task<BrokerResponse> BatchAsync(BrokerRequest request, string session, CancellationToken cancellationToken)
    {
        var arguments = request.Arguments ?? new JsonObject();
        BatchPlan batch;
        try
        {
            batch = BatchPlanner.Plan(arguments, CommandLookup);
        }
        catch (PlanException error)
        {
            return NotStarted(request, error.Code, error.Message);
        }
        var canonicalArguments = (JsonObject)arguments.DeepClone();
        foreach (var key in CanonicalJson.ControlFields) canonicalArguments.Remove(key);

        ConfirmationProposal? proposal = null;
        if (!string.IsNullOrWhiteSpace(request.ConfirmationId))
        {
            var check = _confirmations.Validate(request.ConfirmationId, session, "batch.run", canonicalArguments);
            if (!check.Ok) return NotStarted(request, check.ErrorCode!, check.Message!);
            proposal = check.Proposal;
        }
        var operationId = ResolveOperationId(request, proposal);
        request = request with { OperationId = operationId };

        var outer = await PlanBatchChildrenAsync(batch, session, operationId, cancellationToken).ConfigureAwait(false);
        var outerHash = CanonicalJson.Hash("batch-plan", outer.Json);
        if (proposal is null)
        {
            var begin = _journal.Begin(session, operationId, outerHash, "batch.run", outer.Json, outer.Risk == RiskLevel.Confirm);
            var early = JournalShortCircuit(request, begin);
            if (early is not null) return early;
            if (outer.Risk == RiskLevel.Confirm)
            {
                var prepared = _confirmations.Prepare(session, "batch.run", canonicalArguments,
                    $"Run a batch of {batch.Children.Count} operations including confirm-required changes. No operation runs until this whole plan is confirmed.",
                    operationId, outerHash, outer.Json);
                return new BrokerResponse(request.Id, ResultStatus.RequiresConfirmation,
                    JsonSerializer.SerializeToNode(prepared, PipeProtocol.JsonOptions), null,
                    new OperationMetadata("batch_planner", false, null, null,
                        ["No operation was run. Every child was planned; commit the same batch with confirmation_id."], _driver.Revision),
                    ExecutionState.NotStarted, operationId);
            }
        }
        else if (!string.Equals(proposal.PlanHash, outerHash, StringComparison.Ordinal))
        {
            return NotStarted(request, ErrorCodes.TargetChanged,
                "A batch target or precondition changed after approval. No operation was run and the approval was kept.");
        }

        return await _coordinator.RunExclusiveAsync("batch", async () =>
        {
            var inner = await PlanBatchChildrenAsync(batch, session, operationId, cancellationToken).ConfigureAwait(false);
            var innerHash = CanonicalJson.Hash("batch-plan", inner.Json);
            if (proposal is not null && !string.Equals(proposal.PlanHash, innerHash, StringComparison.Ordinal))
                return NotStarted(request, ErrorCodes.TargetChanged,
                    "A batch target or precondition changed while waiting for the mutation lease. No operation was run.");
            if (proposal is null && inner.Risk == RiskLevel.Confirm)
                return NotStarted(request, ErrorCodes.TargetChanged,
                    "Cubase state changed so that this batch now needs confirmation. No operation was run; request it again.");
            var begin = _journal.Begin(session, operationId, proposal?.PlanHash ?? innerHash, "batch.run", inner.Json, false);
            var early = JournalShortCircuit(request, begin);
            if (early is not null) return early;
            var entry = begin.Entry;
            var approvalConsumed = proposal is null;
            var journalDispatching = false;
            bool FirstDispatch()
            {
                if (!approvalConsumed)
                {
                    if (!_confirmations.TryConsume(proposal!.ConfirmationId)) return false;
                    approvalConsumed = true;
                }
                if (!journalDispatching)
                {
                    entry = _journal.MarkDispatching(entry);
                    journalDispatching = true;
                }
                return true;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(batch.TimeoutMs);
            var results = new List<BatchChildResult>();
            var byId = new Dictionary<string, BatchChildResult>(StringComparer.Ordinal);
            var responses = new JsonArray();
            string? stopReason = null;
            foreach (var child in batch.Children)
            {
                if (timeout.IsCancellationRequested) { stopReason = "timeout"; break; }
                if (!BatchOutcomeReducer.CanRun(child, byId))
                {
                    var skipped = new BatchChildResult(child.Id, child.Index, ResultStatus.Error, ExecutionState.NotStarted,
                        "DEPENDENCY_NOT_VERIFIED", false, null, "dependency_not_verified");
                    results.Add(skipped);
                    byId[child.Id] = skipped;
                    responses.Add(new JsonObject { ["id"] = child.Id, ["index"] = child.Index, ["skipped"] = "dependency_not_verified" });
                    continue;
                }
                var childOperationId = ChildOperationId(operationId, child.Id);
                var childRequest = new BrokerRequest($"{request.Id}:{child.Id}", child.OperationName, child.Operation.Arguments,
                    null, session, request.ClientProtocolVersion, request.ClientReleaseId, childOperationId);
                var childContext = new BatchChildContext(childOperationId, inner.TargetHashes.GetValueOrDefault(child.Id), FirstDispatch);
                BrokerResponse response;
                if (child.Operation.IsHostMutation)
                {
                    response = await HostMutationAsync(childRequest, child.Operation, session, childContext, timeout.Token).ConfigureAwait(false);
                }
                else if (child.Operation.Scope == OperationScope.Document)
                {
                    response = BrokerResponse.Success(childRequest.Id, _compositions.Execute(child.Operation.Action, child.Operation.Arguments, session),
                        "midi_composition+smf", true, executionState: ExecutionState.Applied);
                }
                else
                {
                    response = await ReadAsync(childRequest, child.Operation, session, timeout.Token).ConfigureAwait(false);
                }
                var result = new BatchChildResult(child.Id, child.Index, response.Status,
                    response.ExecutionState ?? ExecutionState.Unknown, response.Error?.Code, childContext.Dispatched,
                    JsonSerializer.SerializeToNode(response, PipeProtocol.JsonOptions));
                results.Add(result);
                byId[child.Id] = result;
                responses.Add(new JsonObject
                {
                    ["id"] = child.Id, ["index"] = child.Index, ["operation_id"] = childOperationId,
                    ["execution_state"] = result.ExecutionState, ["mutation_sent"] = result.MutationSent,
                    ["response"] = result.Response?.DeepClone()
                });
                if (journalDispatching) entry = _journal.UpdateChildren(entry, responses);
                var (next, reason) = BatchOutcomeReducer.AfterChild(result, batch.ContinueOnUnverified);
                if (next == BatchOutcomeReducer.Next.Stop) { stopReason = reason; break; }
            }
            if (stopReason is null && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                && results.Count < batch.Children.Count) stopReason = "timeout";

            var summary = BatchOutcomeReducer.Summarize(batch.Children.Count, results, stopReason ?? "completed");
            var executionState = results.Any(result => result.ExecutionState == ExecutionState.Unknown) ? ExecutionState.Unknown
                : summary.Dispatched == 0 ? ExecutionState.NotStarted
                : summary.Outcome == "partially_applied" ? ExecutionState.PartiallyApplied
                : ExecutionState.Applied;
            var data = new JsonObject
            {
                ["responses"] = responses,
                ["outcome"] = summary.Outcome,
                ["stop_reason"] = summary.StopReason,
                ["stopped_after_index"] = summary.StoppedAfterIndex,
                ["requested_count"] = summary.Requested,
                ["evaluated_count"] = summary.Evaluated,
                ["dispatched_count"] = summary.Dispatched,
                ["applied_count"] = summary.Applied,
                ["verified_count"] = summary.Verified,
                ["unverified_count"] = summary.Unverified,
                ["failed_count"] = summary.Failed,
                ["not_started_count"] = summary.NotStarted,
                ["executed"] = summary.Dispatched,
                ["executed_note"] = "executed is a legacy alias of dispatched_count.",
                ["continue_on_unverified"] = batch.ContinueOnUnverified,
                ["timeout_ms"] = batch.TimeoutMs
            };
            var warnings = new List<string>();
            if (summary.StopReason == "outcome_unknown")
                warnings.Add("A child's outcome is unknown, so the batch stopped. Nothing was re-sent and there is no automatic resume past an unknown child.");
            if (summary.StopReason == "unverified_predecessor")
                warnings.Add("A child applied a change that could not be verified; later children were not run. continue_on_unverified=true only lets independent children (depends_on=[]) run.");
            if (summary.StopReason is "failed" or "context_changed")
                warnings.Add("The batch stopped at a failed child. Completed children are recorded and are never re-run by re-submitting this operation_id; no automatic Undo was performed.");
            if (summary.StopReason == "timeout")
                warnings.Add($"The batch reached its {batch.TimeoutMs} ms limit; remaining children were not started.");
            var failure = summary.Status is ResultStatus.Error or ResultStatus.Offline or ResultStatus.Unsupported
                ? new BrokerError(results.LastOrDefault(result => result.ErrorCode is not null)?.ErrorCode ?? "BATCH_FAILED",
                    $"Batch outcome: {summary.Outcome}; stop reason: {summary.StopReason}.")
                : summary.Status == ResultStatus.Unverified && summary.Outcome == "outcome_unknown"
                    ? new BrokerError(ErrorCodes.OutcomeUnknown, "A batch child's outcome is unknown.")
                    : null;
            var batchResponse = new BrokerResponse(request.Id, summary.Status, data, failure,
                new OperationMetadata("batch", summary.Status == ResultStatus.Ok, null, null, warnings, _driver.Revision),
                executionState, operationId);
            if (journalDispatching || summary.Status == ResultStatus.Ok) _journal.Complete(entry, batchResponse, responses);
            else _journal.DiscardUndispatched(entry);
            return batchResponse;
        }, refusal => NotStarted(request, refusal.ErrorCode, refusal.Message), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read-only resume plan: remaining children of a recorded batch. Automatic resume is not supported.</summary>
    private BrokerResponse BatchResumePlan(BrokerRequest request, JsonObject args, string session)
    {
        var id = args["resume_operation_id"]?.GetValue<string>() ?? throw new PlanException(ErrorCodes.InvalidArgument, "resume_operation_id is required.");
        var entry = _journal.Get(session, id);
        if (entry is null) return NotStarted(request, "OPERATION_NOT_FOUND", "No batch with this operation_id is recorded for this client session.");
        var planned = (entry.ResolvedTargets?["children"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var done = (entry.Children as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var unknown = done.Where(child => child["execution_state"]?.GetValue<string>() == ExecutionState.Unknown)
            .Select(child => child["id"]?.GetValue<string>()).ToArray();
        var finished = done.Where(child => child["execution_state"]?.GetValue<string>() is ExecutionState.Applied or ExecutionState.NotApplied
                && child["response"]?["status"]?.GetValue<string>() == nameof(ResultStatus.Ok))
            .Select(child => child["id"]?.GetValue<string>()).ToHashSet();
        var attempted = done.Select(child => child["id"]?.GetValue<string>()).ToHashSet();
        var remaining = planned.Where(child => !attempted.Contains(child["id"]?.GetValue<string>()))
            .Select(child => (JsonNode?)child.DeepClone()).ToArray();
        var data = new JsonObject
        {
            ["batch_operation_id"] = entry.OperationId,
            ["journal_state"] = entry.State,
            ["resume_supported"] = false,
            ["blocked_by_unknown_children"] = new JsonArray(unknown.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            ["completed_children"] = new JsonArray(finished.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            ["remaining_children"] = new JsonArray(remaining),
            ["instructions"] = unknown.Length > 0
                ? "At least one child has an unknown outcome. Inspect Cubase before planning anything else; the remaining children cannot be resumed automatically."
                : "Submit the remaining children as a new batch with a new operation_id. Completed children are never re-run by the journal."
        };
        return BrokerResponse.Success(request.Id, data, "execution_journal", true, executionState: ExecutionState.NotApplicable);
    }
}
