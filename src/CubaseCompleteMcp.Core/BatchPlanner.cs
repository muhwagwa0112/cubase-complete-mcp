using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

public sealed record BatchChildPlan(string Id, int Index, NormalizedOperation Operation, string[] DependsOn,
    string OperationName);

public sealed record BatchPlan(IReadOnlyList<BatchChildPlan> Children, bool ContinueOnUnverified, int TimeoutMs)
{
    public RiskLevel Risk => Children.Any(child => child.Operation.Risk == RiskLevel.Confirm) ? RiskLevel.Confirm
        : Children.Any(child => child.Operation.Risk == RiskLevel.Low) ? RiskLevel.Low : RiskLevel.None;

    public JsonObject ToJson() => new()
    {
        ["continue_on_unverified"] = ContinueOnUnverified,
        ["timeout_ms"] = TimeoutMs,
        ["children"] = new JsonArray(Children.Select(child => (JsonNode?)new JsonObject
        {
            ["id"] = child.Id, ["index"] = child.Index, ["operation"] = child.OperationName,
            ["action"] = child.Operation.Action, ["risk"] = child.Operation.Risk.ToString(),
            ["classified"] = child.Operation.Classified, ["impact"] = child.Operation.Impact,
            ["depends_on"] = new JsonArray(child.DependsOn.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
        }).ToArray())
    };
}

/// <summary>
/// Plans a whole batch before anything runs: every child is normalized, risk-classified and dependency-checked.
/// Nested batches, empty or oversized batches, duplicate IDs, unknown or forward/cyclic dependencies and unknown
/// operations are refused here, so a malformed batch never dispatches its first child.
/// </summary>
public static class BatchPlanner
{
    public const int MaximumChildren = 32;
    public const int MaximumTimeoutMs = 180_000;

    public static BatchPlan Plan(JsonObject arguments, Func<JsonObject, KeyCommandDescriptor?>? commandLookup = null)
    {
        if (arguments["operations"] is not JsonArray operations)
            throw new PlanException(ErrorCodes.BatchInvalid, "operations must be a JSON array.");
        if (operations.Count == 0) throw new PlanException(ErrorCodes.BatchInvalid, "operations must not be empty.");
        if (operations.Count > MaximumChildren)
            throw new PlanException(ErrorCodes.BatchInvalid, $"A batch accepts at most {MaximumChildren} operations; received {operations.Count}.");
        var continueOnUnverified = arguments["continue_on_unverified"]?.GetValue<bool>() ?? false;
        var timeoutMs = arguments["timeout_ms"]?.GetValue<int>() ?? MaximumTimeoutMs;
        if (timeoutMs is < 1 or > MaximumTimeoutMs)
            throw new PlanException(ErrorCodes.BatchInvalid, $"timeout_ms must be between 1 and {MaximumTimeoutMs}.");

        var children = new List<BatchChildPlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index] is not JsonObject item)
                throw new PlanException(ErrorCodes.BatchInvalid, $"operations[{index}] must be an object.");
            var id = item["id"]?.GetValue<string>() ?? $"op{index}";
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                throw new PlanException(ErrorCodes.BatchInvalid, $"operations[{index}] has an empty or duplicate id '{id}'.");
            var operationName = item["operation"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                ?? throw new PlanException(ErrorCodes.BatchInvalid, $"operations[{index}].operation is required.");
            if (operationName == "batch")
                throw new PlanException(ErrorCodes.BatchInvalid, "Nested batches are not supported.");
            string[] dependsOn;
            if (item["depends_on"] is JsonArray explicitDependencies)
            {
                dependsOn = explicitDependencies.Select(node => node?.GetValue<string>()
                    ?? throw new PlanException(ErrorCodes.BatchInvalid, $"operations[{index}].depends_on contains a non-string.")).ToArray();
                foreach (var dependency in dependsOn)
                    if (!children.Any(child => child.Id == dependency))
                        throw new PlanException(ErrorCodes.BatchInvalid,
                            $"operations[{index}] depends on '{dependency}', which is not an earlier operation in this batch.");
            }
            else if (item.ContainsKey("depends_on") && item["depends_on"] is not null)
            {
                throw new PlanException(ErrorCodes.BatchInvalid, $"operations[{index}].depends_on must be an array.");
            }
            else
            {
                // Linear input: each operation depends on the one before it.
                dependsOn = index == 0 ? [] : [children[index - 1].Id];
            }

            NormalizedOperation normalized;
            try
            {
                normalized = OperationPlanner.Normalize(operationName, item["arguments"] as JsonObject ?? new JsonObject(), commandLookup);
            }
            catch (PlanException error)
            {
                throw new PlanException(error.Code, $"operations[{index}] ({operationName}): {error.Message}");
            }
            if (normalized.Family == "ui.inspect")
                throw new PlanException(ErrorCodes.BatchInvalid, "ui.inspect creates single-use snapshots and cannot run inside a batch.");
            children.Add(new BatchChildPlan(id, index, normalized, dependsOn, operationName));
        }
        return new BatchPlan(children, continueOnUnverified, timeoutMs);
    }
}

public sealed record BatchChildResult(
    string Id,
    int Index,
    ResultStatus Status,
    string ExecutionState,
    string? ErrorCode,
    bool MutationSent,
    JsonNode? Response,
    string? SkippedReason = null);

public sealed record BatchSummary(
    ResultStatus Status,
    string Outcome,
    string StopReason,
    int? StoppedAfterIndex,
    int Requested,
    int Evaluated,
    int Dispatched,
    int Applied,
    int Verified,
    int Unverified,
    int Failed,
    int NotStarted);

/// <summary>Pure decision rules for sequential batch execution and result aggregation.</summary>
public static class BatchOutcomeReducer
{
    public enum Next { Continue, Stop }

    /// <summary>
    /// A child may run only when every dependency (transitively through depends_on) finished verified, or when
    /// continue_on_unverified allows it and it has no dependency on an unverified child.
    /// </summary>
    public static bool CanRun(BatchChildPlan child, IReadOnlyDictionary<string, BatchChildResult> results) =>
        child.DependsOn.All(dependency => results.TryGetValue(dependency, out var result) && result.Status == ResultStatus.Ok);

    public static (Next Next, string? StopReason) AfterChild(BatchChildResult result, bool continueOnUnverified)
    {
        if (result.Status == ResultStatus.Ok) return (Next.Continue, null);
        if (result.Status == ResultStatus.RequiresConfirmation) return (Next.Stop, "confirmation_required");
        if (result.ExecutionState == ExecutionState.Unknown) return (Next.Stop, "outcome_unknown");
        if (result.ErrorCode is ErrorCodes.TargetChanged or ErrorCodes.StaleTarget or ErrorCodes.ClientSessionLost
            or ErrorCodes.DriverContractMismatch)
            return (Next.Stop, "context_changed");
        if (result.Status == ResultStatus.Unverified)
            return result.ExecutionState is ExecutionState.Applied or ExecutionState.NotApplied && continueOnUnverified
                ? (Next.Continue, null)
                : (Next.Stop, "unverified_predecessor");
        return (Next.Stop, "failed");
    }

    public static BatchSummary Summarize(int requested, IReadOnlyList<BatchChildResult> results, string stopReason)
    {
        var dispatched = results.Count(result => result.MutationSent);
        var applied = results.Count(result => result.ExecutionState is ExecutionState.Applied or ExecutionState.PartiallyApplied);
        var verified = results.Count(result => result.Status == ResultStatus.Ok);
        var unverified = results.Count(result => result.Status == ResultStatus.Unverified);
        var executed = results.Where(result => result.SkippedReason is null).ToArray();
        var failed = executed.Count(result => result.Status is ResultStatus.Error or ResultStatus.Offline or ResultStatus.Unsupported);
        var notStarted = requested - results.Count(result => result.SkippedReason is null);
        var anyUnknown = results.Any(result => result.ExecutionState == ExecutionState.Unknown);
        var priorApplied = results.Any(result => result.MutationSent || result.ExecutionState is ExecutionState.Applied or ExecutionState.PartiallyApplied);
        var failing = executed.LastOrDefault(result => result.Status is ResultStatus.Error or ResultStatus.Offline or ResultStatus.Unsupported);

        ResultStatus status;
        string outcome;
        if (stopReason == "confirmation_required") { status = ResultStatus.RequiresConfirmation; outcome = "awaiting_confirmation"; }
        else if (anyUnknown) { status = ResultStatus.Unverified; outcome = "outcome_unknown"; }
        else if (failing is not null || stopReason == "context_changed")
        {
            outcome = priorApplied ? "partially_applied" : "failed";
            status = !priorApplied && failing?.Status is ResultStatus.Offline or ResultStatus.Unsupported
                ? failing.Status : ResultStatus.Error;
        }
        else if (verified == requested) { status = ResultStatus.Ok; outcome = "verified"; }
        else { status = ResultStatus.Unverified; outcome = "unverified"; }

        var executedCount = results.Count(result => result.SkippedReason is null);
        return new BatchSummary(status, outcome, stopReason,
            stopReason == "completed" ? null : executedCount == 0 ? null : results.Last(result => result.SkippedReason is null).Index,
            requested, requested, dispatched, applied, verified, unverified, failed, notStarted);
    }
}
