using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

public static class TargetKinds
{
    public const string MixerChannel = "mixer_channel";
    public const string SelectedTrack = "selected_track";
    public const string Project = "project";
    public const string PluginSlot = "plugin_slot";
}

/// <summary>
/// Host identity a handle is bound to. <see cref="BankIndex"/> is only the 0-63 MixConsole bank slot where the
/// object was observed; it is an address, not an identity. Identity is <see cref="UniqueId"/> plus
/// <see cref="RuntimeId"/> within one driver epoch and project epoch.
/// </summary>
public sealed record TargetIdentity(
    string Kind,
    string Root,
    string? UniqueId,
    long? RuntimeId,
    int? BankIndex,
    string? Title,
    long? ObjectId = null)
{
    public bool HasStableIdentity => !string.IsNullOrEmpty(UniqueId) && RuntimeId is not null;

    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind, ["root"] = Root, ["unique_id"] = UniqueId, ["runtime_id"] = RuntimeId,
        ["bank_index"] = BankIndex, ["title"] = Title, ["object_id"] = ObjectId
    };

    /// <summary>Same host object: identical unique ID and runtime ID. Titles and bank slots may differ.</summary>
    public bool SameObjectAs(TargetIdentity other) =>
        HasStableIdentity && other.HasStableIdentity
        && Kind == other.Kind
        && string.Equals(UniqueId, other.UniqueId, StringComparison.Ordinal)
        && RuntimeId == other.RuntimeId;
}

public sealed record HostEpochs(string BrokerInstanceId, long DriverEpoch, string ProjectEpoch);

public sealed record TargetHandle(
    string Handle,
    string ClientSessionOwner,
    HostEpochs Epochs,
    TargetIdentity Identity,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    public JsonObject ToPublicJson() => new()
    {
        ["handle"] = Handle, ["kind"] = Identity.Kind, ["root"] = Identity.Root,
        ["bank_index"] = Identity.BankIndex, ["title"] = Identity.Title,
        ["unique_id"] = Identity.UniqueId, ["runtime_id"] = Identity.RuntimeId,
        ["driver_epoch"] = Epochs.DriverEpoch, ["project_epoch"] = Epochs.ProjectEpoch,
        ["expires_at"] = ExpiresAt.ToString("O")
    };
}

public sealed record TargetResolution(bool Ok, TargetHandle? Handle, string? ErrorCode, string? Message)
{
    public static TargetResolution Success(TargetHandle handle) => new(true, handle, null, null);
    public static TargetResolution Failure(string code, string message) => new(false, null, code, message);
}

/// <summary>
/// Issues opaque random handles for host objects. A handle belongs to one client session and one
/// broker/driver/project epoch; any epoch change invalidates it. Callers never supply identities themselves: the
/// store's own record is the only source, and the host must be re-read before a write uses it.
/// </summary>
public sealed class TargetHandleStore(TimeSpan? lifetime = null, Func<DateTimeOffset>? clock = null)
{
    private readonly ConcurrentDictionary<string, TargetHandle> _handles = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public int Count => _handles.Count;

    public TargetHandle Issue(string clientSessionId, HostEpochs epochs, TargetIdentity identity)
    {
        Prune();
        var now = _clock();
        var handle = new TargetHandle("tgt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            clientSessionId, epochs, identity, now, now + _lifetime);
        _handles[handle.Handle] = handle;
        return handle;
    }

    public TargetResolution Resolve(string? handle, string clientSessionId, HostEpochs current)
    {
        Prune();
        if (string.IsNullOrWhiteSpace(handle) || !_handles.TryGetValue(handle, out var entry))
            return TargetResolution.Failure(ErrorCodes.StaleTarget,
                "The target handle is unknown or expired. List targets again to obtain a fresh handle.");
        if (!entry.ClientSessionOwner.Equals(clientSessionId, StringComparison.Ordinal))
            return TargetResolution.Failure(ErrorCodes.TargetScopeMismatch,
                "The target handle belongs to another client session.");
        if (entry.ExpiresAt < _clock())
        {
            _handles.TryRemove(handle, out _);
            return TargetResolution.Failure(ErrorCodes.StaleTarget, "The target handle expired. List targets again.");
        }
        if (!entry.Epochs.BrokerInstanceId.Equals(current.BrokerInstanceId, StringComparison.Ordinal)
            || entry.Epochs.DriverEpoch != current.DriverEpoch
            || !entry.Epochs.ProjectEpoch.Equals(current.ProjectEpoch, StringComparison.Ordinal))
        {
            _handles.TryRemove(handle, out _);
            return TargetResolution.Failure(ErrorCodes.StaleTarget,
                "The driver session or the open project changed after this handle was issued. List targets again.");
        }
        return TargetResolution.Success(entry);
    }

    /// <summary>Drops every handle issued in an older epoch, e.g. after a driver reconnect or project switch.</summary>
    public int InvalidateOutside(HostEpochs current)
    {
        var removed = 0;
        foreach (var pair in _handles)
        {
            if (pair.Value.Epochs != current && _handles.TryRemove(pair.Key, out _)) removed++;
        }
        return removed;
    }

    private void Prune()
    {
        var now = _clock();
        foreach (var pair in _handles)
            if (pair.Value.ExpiresAt < now) _handles.TryRemove(pair.Key, out _);
    }
}
