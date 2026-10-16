using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CubaseCompleteMcp.Core;

public static class JournalState
{
    public const string AwaitingConfirmation = "awaiting_confirmation";
    public const string Prepared = "prepared";
    public const string Dispatching = "dispatching";
    public const string Completed = "completed";
    public const string Unknown = "unknown";
}

public sealed record JournalEntry(
    int SchemaVersion,
    string OperationId,
    string OwnerHash,
    string PlanHash,
    string Operation,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    JsonNode? ResolvedTargets = null,
    JsonNode? Result = null,
    string? ResultStatus = null,
    string? ExecutionState = null,
    JsonNode? Children = null,
    string? CandidateSource = null,
    string? RecoveryNote = null);

public enum JournalBeginKind { New, Resume, ReplayCompleted, OutcomeUnknown, Conflict }

public sealed record JournalBeginResult(JournalBeginKind Kind, JournalEntry Entry);

/// <summary>
/// Per-user execution journal for business-level operation IDs. It records the canonical plan hash, the owning
/// client session (as a hash), resolved targets and the dispatch state, flushing <c>dispatching</c> to disk before
/// any external call. It lets the broker return the recorded result for an identical retry, reject a reused ID with
/// a different plan, and report crash-interrupted dispatches as unknown. It does not create exactly-once semantics
/// in Cubase, and nothing is ever re-dispatched from the journal.
/// </summary>
public sealed class ExecutionJournal
{
    public const int SchemaVersion = 1;
    private static readonly Regex WindowsPath = new(@"^(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.Compiled);
    private readonly string _root;
    private readonly object _gate = new();
    private readonly string? _candidateSource;

    public ExecutionJournal(string root, string? candidateSource = null)
    {
        _root = Path.GetFullPath(root);
        _candidateSource = candidateSource;
        Directory.CreateDirectory(_root);
        RecoverInterruptedDispatches();
    }

    public string Root => _root;

    public static string OwnerHash(string clientSessionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("owner\n" + clientSessionId))).ToLowerInvariant()[..32];

    /// <summary>Registers an operation or classifies a repeat of it. Never overwrites a completed or unknown entry.</summary>
    public JournalBeginResult Begin(string clientSessionId, string operationId, string planHash, string operation,
        JsonNode? resolvedTargets, bool awaitingConfirmation)
    {
        ValidateOperationId(operationId);
        lock (_gate)
        {
            var owner = OwnerHash(clientSessionId);
            var existing = ReadEntry(owner, operationId);
            if (existing is not null)
            {
                if (!existing.PlanHash.Equals(planHash, StringComparison.Ordinal))
                    return new JournalBeginResult(JournalBeginKind.Conflict, existing);
                return existing.State switch
                {
                    JournalState.Completed => new JournalBeginResult(JournalBeginKind.ReplayCompleted, existing),
                    JournalState.Dispatching or JournalState.Unknown => new JournalBeginResult(JournalBeginKind.OutcomeUnknown, existing),
                    _ => new JournalBeginResult(JournalBeginKind.Resume, Write(existing with
                    {
                        State = awaitingConfirmation ? JournalState.AwaitingConfirmation : JournalState.Prepared,
                        ResolvedTargets = resolvedTargets?.DeepClone(), UpdatedAt = DateTimeOffset.UtcNow
                    }))
                };
            }
            var now = DateTimeOffset.UtcNow;
            var entry = new JournalEntry(SchemaVersion, operationId, owner, planHash, operation,
                awaitingConfirmation ? JournalState.AwaitingConfirmation : JournalState.Prepared, now, now,
                Redact(resolvedTargets), CandidateSource: _candidateSource);
            return new JournalBeginResult(JournalBeginKind.New, Write(entry));
        }
    }

    /// <summary>Flushed to disk before the first external call so a crash is recovered as unknown, never replayed.</summary>
    public JournalEntry MarkDispatching(JournalEntry entry)
    {
        lock (_gate)
        {
            return Write(entry with { State = JournalState.Dispatching, UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    public JournalEntry Complete(JournalEntry entry, BrokerResponse response, JsonNode? children = null)
    {
        lock (_gate)
        {
            var state = response.ExecutionState == Core.ExecutionState.Unknown ? JournalState.Unknown : JournalState.Completed;
            return Write(entry with
            {
                State = state, UpdatedAt = DateTimeOffset.UtcNow,
                Result = Redact(JsonSerializer.SerializeToNode(response, PipeProtocol.JsonOptions)),
                ResultStatus = response.Status.ToString(), ExecutionState = response.ExecutionState,
                Children = Redact(children)
            });
        }
    }

    /// <summary>Records child progress of a batch without finishing it, so a completed prefix is fixed on disk.</summary>
    public JournalEntry UpdateChildren(JournalEntry entry, JsonNode children)
    {
        lock (_gate)
        {
            return Write(entry with { Children = Redact(children), UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    public JournalEntry? Get(string clientSessionId, string operationId)
    {
        ValidateOperationId(operationId);
        lock (_gate)
        {
            return ReadEntry(OwnerHash(clientSessionId), operationId);
        }
    }

    /// <summary>Abandons a plan that never dispatched (for example a refused preflight) so the ID can be re-planned.</summary>
    public void DiscardUndispatched(JournalEntry entry)
    {
        lock (_gate)
        {
            var current = ReadEntry(entry.OwnerHash, entry.OperationId);
            if (current?.State is JournalState.Prepared or JournalState.AwaitingConfirmation)
                File.Delete(EntryPath(entry.OwnerHash, entry.OperationId));
        }
    }

    public static void ValidateOperationId(string operationId)
    {
        if (!Guid.TryParse(operationId, out _))
            throw new ArgumentException("operation_id must be a UUID string.");
    }

    /// <summary>Replaces local absolute paths with a short digest and bounds long free text before it is persisted.</summary>
    public static JsonNode? Redact(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var pair in obj) copy[pair.Key] = Redact(pair.Value);
                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(Redact).ToArray());
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (WindowsPath.IsMatch(text) || text.StartsWith('/'))
                    return JsonValue.Create("<path:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant()
                        + ":" + Path.GetFileName(text.TrimEnd('\\', '/')) + ">");
                return JsonValue.Create(text.Length > 512 ? text[..512] + "…" : text);
            default:
                return node.DeepClone();
        }
    }

    private void RecoverInterruptedDispatches()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(file), PipeProtocol.JsonOptions);
                if (entry?.State != JournalState.Dispatching) continue;
                Write(entry with
                {
                    State = JournalState.Unknown, UpdatedAt = DateTimeOffset.UtcNow,
                    ExecutionState = Core.ExecutionState.Unknown,
                    RecoveryNote = "The broker stopped while this operation was dispatching. Its outcome is unknown and it was not re-sent."
                });
            }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
    }

    private JournalEntry? ReadEntry(string owner, string operationId)
    {
        var path = EntryPath(owner, operationId);
        if (!File.Exists(path)) return null;
        var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(path), PipeProtocol.JsonOptions);
        return entry is not null && entry.OwnerHash == owner && entry.OperationId == operationId ? entry : null;
    }

    private JournalEntry Write(JournalEntry entry)
    {
        var path = EntryPath(entry.OwnerHash, entry.OperationId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, PipeProtocol.JsonOptions);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        return entry;
    }

    private string EntryPath(string owner, string operationId) =>
        Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner + "\n" + operationId)))
            .ToLowerInvariant()[..40] + ".json");
}
