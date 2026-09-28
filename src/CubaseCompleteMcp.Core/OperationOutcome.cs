namespace CubaseCompleteMcp.Core;

/// <summary>What is known about whether a requested change reached Cubase.</summary>
public static class ExecutionState
{
    /// <summary>Reads and document-only operations that never change the host.</summary>
    public const string NotApplicable = "not_applicable";
    /// <summary>Nothing was handed to Cubase; the request can be re-planned safely.</summary>
    public const string NotStarted = "not_started";
    /// <summary>The target already satisfied the goal (fresh read), so no change was sent.</summary>
    public const string NotApplied = "not_applied";
    /// <summary>The change was observed on the host.</summary>
    public const string Applied = "applied";
    /// <summary>The change may have been handed to Cubase but its effect is not known. Never re-sent automatically.</summary>
    public const string Unknown = "unknown";
    /// <summary>A multi-step workflow stopped after some steps were applied.</summary>
    public const string PartiallyApplied = "partially_applied";
    /// <summary>The host explicitly rejected the change before applying it.</summary>
    public const string Rejected = "rejected";
}

public static class ErrorCodes
{
    public const string BrokerProtocolMismatch = "BROKER_PROTOCOL_MISMATCH";
    public const string DriverContractMismatch = "DRIVER_CONTRACT_MISMATCH";
    public const string DriverOffline = "DRIVER_OFFLINE";
    public const string OutcomeUnknown = "OUTCOME_UNKNOWN";
    public const string ClientSessionLost = "CLIENT_SESSION_LOST";
    public const string StaleTarget = "STALE_TARGET";
    public const string TargetChanged = "TARGET_CHANGED";
    public const string TargetContextUnsafe = "TARGET_CONTEXT_UNSAFE";
    public const string TargetScopeMismatch = "TARGET_SCOPE_MISMATCH";
    public const string TargetRequired = "TARGET_REQUIRED";
    public const string TargetNotSelected = "TARGET_NOT_SELECTED";
    public const string LegacyTargetAmbiguous = "LEGACY_TARGET_AMBIGUOUS";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string Busy = "BUSY";
    public const string PostconditionTimeout = "POSTCONDITION_TIMEOUT";
    public const string UnknownCommandRisk = "UNKNOWN_COMMAND_RISK";
    public const string CommandNotFound = "COMMAND_NOT_FOUND";
    public const string FileExists = "FILE_EXISTS";
    public const string FileChanged = "FILE_CHANGED";
    public const string PluginCatalogUnavailable = "PLUGIN_CATALOG_UNAVAILABLE";
    public const string ImportCausalityUnproven = "IMPORT_CAUSALITY_UNPROVEN";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string InvalidAction = "INVALID_ACTION";
    public const string ConfirmationMismatch = "CONFIRMATION_MISMATCH";
    public const string ConfirmationUnavailable = "CONFIRMATION_UNAVAILABLE";
    public const string StateUnknown = "STATE_UNKNOWN";
    public const string UnsupportedTimeFormat = "UNSUPPORTED_TIME_FORMAT";
    public const string ParameterAmbiguous = "PARAMETER_AMBIGUOUS";
    public const string ParameterLocked = "PARAMETER_LOCKED";
    public const string BatchInvalid = "BATCH_INVALID";
    public const string StageTimeout = "WORKFLOW_STAGE_TIMEOUT";
    public const string StageControlMissing = "WORKFLOW_CONTROL_MISSING";
    public const string TransportActive = "TRANSPORT_ACTIVE";
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// Decides which broker requests may be re-sent on a fresh pipe after a connection loss. Only stateless
/// diagnostics qualify: they neither change Cubase nor depend on the broker-side client session (confirmations,
/// target handles, compositions, journal entries), all of which are bound to the pipe connection that was lost.
/// </summary>
public static class OperationReplayPolicy
{
    private static readonly HashSet<string> StatelessDiagnosticReads = new(StringComparer.Ordinal)
    {
        "status", "capabilities.search", "capabilities.list", "commands.list", "diagnostics"
    };

    public static bool IsStatelessDiagnosticRead(BrokerRequest request) =>
        StatelessDiagnosticReads.Contains(request.Operation.Trim().ToLowerInvariant())
        && string.IsNullOrEmpty(request.ConfirmationId)
        && !HasSessionScopedArgument(request);

    private static readonly HashSet<string> ReadActions = new(StringComparer.Ordinal)
    {
        "get", "list", "inspect", "status", "catalog", "can_perform", "list_documents", "get_document",
        "inspect_file", "operation_status", "resume_plan", "inspect_slot"
    };

    /// <summary>
    /// True only when the request provably cannot change Cubase or broker state. Everything else is treated as
    /// a potential mutation, so a lost response yields <see cref="ErrorCodes.OutcomeUnknown"/> instead of a replay.
    /// </summary>
    public static bool IsProvablyReadOnly(BrokerRequest request)
    {
        var operation = request.Operation.Trim().ToLowerInvariant();
        if (StatelessDiagnosticReads.Contains(operation) || operation == "ui.inspect") return true;
        if (operation is "batch") return request.Arguments?["resume_operation_id"] is not null
            && request.Arguments?["operations"] is null;
        if (operation is "ui.invoke" or "command") return false;
        var action = request.Arguments?["action"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (action is null) return operation is "project" or "transport" or "mixer" or "track" or "plugin" or "direct";
        return ReadActions.Contains(action);
    }

    private static bool HasSessionScopedArgument(BrokerRequest request) =>
        request.Arguments is not null && (request.Arguments.ContainsKey("operation_id")
            || (request.Arguments["action"]?.GetValue<string>() is { } action
                && !action.Equals("status", StringComparison.OrdinalIgnoreCase)));
}
