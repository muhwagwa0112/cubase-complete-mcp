using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

public static class SafetyPolicy
{
    private static readonly string[] ConfirmOperations =
    [
        "track.delete", "project.close_unsaved", "project.overwrite", "project.record_start",
        "plugin.bulk_replace", "batch.destructive", "ui.delete", "ui.overwrite"
    ];

    private static readonly HashSet<string> ReadOperations = new(StringComparer.Ordinal)
    {
        "status", "diagnostics", "capabilities.search", "capabilities.list", "commands.list"
    };

    /// <summary>
    /// Coarse classification for dotted operation names. Only an explicit allowlist of reads is risk-free; an
    /// operation no rule recognizes is <see cref="RiskLevel.Confirm"/> rather than silently low risk.
    /// </summary>
    public static RiskLevel Classify(string operation, JsonObject? arguments)
    {
        var normalized = operation.Trim().ToLowerInvariant();
        if (ConfirmOperations.Any(item => normalized.Equals(item, StringComparison.Ordinal)
            || normalized.StartsWith(item + ".", StringComparison.Ordinal)))
            return RiskLevel.Confirm;
        if (normalized is "ui.invoke")
            return arguments?["action"]?.GetValue<string>()?.ToLowerInvariant() == "focus" ? RiskLevel.Low : RiskLevel.Confirm;
        if (ReadOperations.Contains(normalized)) return RiskLevel.None;
        return RiskLevel.Confirm;
    }

    public static string HashArguments(string operation, JsonObject? arguments) =>
        CanonicalJson.Hash("args:" + operation.Trim().ToLowerInvariant(), arguments);
}

public sealed record ConfirmationCheck(bool Ok, string? ErrorCode, string? Message, ConfirmationProposal? Proposal)
{
    public static ConfirmationCheck Valid(ConfirmationProposal proposal) => new(true, null, null, proposal);
    public static ConfirmationCheck Invalid(string code, string message, ConfirmationProposal? proposal = null) =>
        new(false, code, message, proposal);
}

/// <summary>
/// Stores approved plans. Validation never consumes an approval; consumption is a separate atomic step taken
/// immediately before the first external dispatch. A refused preflight therefore leaves the approval usable, and a
/// dispatched request consumes it even if its outcome turns out to be unknown.
/// </summary>
public sealed class ConfirmationStore(TimeSpan? lifetime = null, Func<DateTimeOffset>? clock = null)
{
    private readonly ConcurrentDictionary<string, StoredConfirmation> _items = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public ConfirmationProposal Prepare(string clientSessionId, string operation, JsonObject? arguments, string summary,
        string? operationId = null, string? planHash = null, JsonNode? plan = null)
    {
        Prune();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var item = new ConfirmationProposal(id, operation, RiskLevel.Confirm, summary,
            _clock() + _lifetime, SafetyPolicy.HashArguments(operation, arguments), operationId, planHash, plan?.DeepClone());
        _items[id] = new StoredConfirmation(clientSessionId, item);
        return item;
    }

    /// <summary>Checks an approval against the exact request without consuming it.</summary>
    public ConfirmationCheck Validate(string id, string clientSessionId, string operation, JsonObject? arguments)
    {
        Prune();
        if (!_items.TryGetValue(id, out var item))
            return ConfirmationCheck.Invalid(ErrorCodes.ConfirmationUnavailable,
                "The confirmation is unknown, expired or already used. Request a new plan.");
        if (!item.ClientSessionId.Equals(clientSessionId, StringComparison.Ordinal))
            return ConfirmationCheck.Invalid(ErrorCodes.ConfirmationUnavailable,
                "The confirmation belongs to another client session.");
        if (item.Proposal.ExpiresAt < _clock())
            return ConfirmationCheck.Invalid(ErrorCodes.ConfirmationUnavailable, "The confirmation expired.");
        if (!item.Proposal.Operation.Equals(operation, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(item.Proposal.ArgumentsHash),
                Encoding.ASCII.GetBytes(SafetyPolicy.HashArguments(operation, arguments))))
            return ConfirmationCheck.Invalid(ErrorCodes.ConfirmationMismatch,
                "The request differs from the approved plan. The approval was kept; commit exactly the approved request.",
                item.Proposal);
        return ConfirmationCheck.Valid(item.Proposal);
    }

    /// <summary>Atomically consumes an approval. Only one caller can win.</summary>
    public bool TryConsume(string id) => _items.TryRemove(id, out _);

    /// <summary>Validate-and-consume in one step, for callers that dispatch immediately.</summary>
    public bool Consume(string id, string clientSessionId, string operation, JsonObject? arguments) =>
        Validate(id, clientSessionId, operation, arguments).Ok && TryConsume(id);

    private void Prune()
    {
        var now = _clock();
        foreach (var pair in _items)
        {
            if (pair.Value.Proposal.ExpiresAt < now)
            {
                _items.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record StoredConfirmation(string ClientSessionId, ConfirmationProposal Proposal);
}
