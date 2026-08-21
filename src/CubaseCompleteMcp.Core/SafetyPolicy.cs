using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

public static class SafetyPolicy
{
    private static readonly string[] ConfirmOperations =
    [
        "track.delete", "project.close_unsaved", "project.overwrite", "project.record_start",
        "plugin.bulk_replace", "batch.destructive", "ui.delete", "ui.overwrite"
    ];

    public static RiskLevel Classify(string operation, JsonObject? arguments)
    {
        var normalized = operation.Trim().ToLowerInvariant();
        if (ConfirmOperations.Any(item => normalized.Equals(item, StringComparison.Ordinal)
            || normalized.StartsWith(item + ".", StringComparison.Ordinal)))
        {
            return RiskLevel.Confirm;
        }

        if (normalized is "ui.invoke")
        {
            var action = arguments?["action"]?.GetValue<string>()?.ToLowerInvariant();
            if (action is "delete" or "overwrite" or "record")
            {
                return RiskLevel.Confirm;
            }
        }

        return normalized.EndsWith(".get", StringComparison.Ordinal)
            || normalized is "status" or "diagnostics" or "capabilities.search" or "capabilities.list" or "commands.list"
            ? RiskLevel.None
            : RiskLevel.Low;
    }

    public static string HashArguments(string operation, JsonObject? arguments)
    {
        var canonical = operation + "\n" + (arguments?.ToJsonString(PipeProtocol.JsonOptions) ?? "{}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed class ConfirmationStore(TimeSpan? lifetime = null)
{
    private readonly ConcurrentDictionary<string, StoredConfirmation> _items = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromMinutes(5);

    public ConfirmationProposal Prepare(string clientSessionId, string operation, JsonObject? arguments, string summary)
    {
        Prune();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var item = new ConfirmationProposal(id, operation, RiskLevel.Confirm, summary,
            DateTimeOffset.UtcNow + _lifetime, SafetyPolicy.HashArguments(operation, arguments));
        _items[id] = new StoredConfirmation(clientSessionId, item);
        return item;
    }

    public bool Consume(string id, string clientSessionId, string operation, JsonObject? arguments)
    {
        Prune();
        if (!_items.TryRemove(id, out var item))
        {
            return false;
        }

        return item.Proposal.ExpiresAt >= DateTimeOffset.UtcNow
            && item.ClientSessionId.Equals(clientSessionId, StringComparison.Ordinal)
            && item.Proposal.Operation.Equals(operation, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(item.Proposal.ArgumentsHash),
                Encoding.ASCII.GetBytes(SafetyPolicy.HashArguments(operation, arguments)));
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
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
