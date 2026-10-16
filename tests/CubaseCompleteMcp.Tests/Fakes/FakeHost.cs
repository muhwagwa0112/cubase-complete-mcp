using System.Text.Json.Nodes;
using CubaseCompleteMcp.Broker;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests.Fakes;

/// <summary>A mixer channel of the fake host. BankIndex is only where it currently sits in the 64-slot bank.</summary>
public sealed class FakeChannel(string uniqueId, long runtimeId, string title, int bankIndex)
{
    public string UniqueId { get; set; } = uniqueId;
    public long RuntimeId { get; set; } = runtimeId;
    public string Title { get; set; } = title;
    public int BankIndex { get; set; } = bankIndex;
    public Dictionary<string, double> Parameters { get; } = new(StringComparer.Ordinal)
    {
        ["volume"] = 0.5, ["pan"] = 0.5, ["mute"] = 0, ["solo"] = 0, ["monitor"] = 0, ["record_enable"] = 0
    };
    public bool Locked { get; set; }
    public int DuplicateVolumeTitles { get; set; }
}

/// <summary>
/// Deterministic host model speaking the driver contract-2 JSON operations. It counts every write dispatch so tests
/// can prove "sent once" and "never replayed". Host callbacks can be delayed or suppressed.
/// </summary>
public sealed class FakeDriver : ICubaseDriver
{
    private readonly object _gate = new();
    private long _revision = 1;
    private readonly Dictionary<string, long> _stateRevision = new(StringComparer.Ordinal);

    public List<FakeChannel> Channels { get; } = [];
    public string? SelectedUniqueId { get; set; }
    public JsonObject State { get; } = new()
    {
        ["playing"] = false, ["recording"] = false, ["cycle"] = false, ["metronome"] = false, ["tempo"] = 120.0,
        ["position"] = "1.1.1.0", ["leftLocator"] = "1.1.1.0", ["rightLocator"] = "5.1.1.0"
    };
    public Dictionary<string, string?> TimeFormats { get; } = new() { ["position"] = "Bars+Beats", ["leftLocator"] = "Bars+Beats", ["rightLocator"] = "Bars+Beats" };
    public uint SessionId { get; private set; } = 1001;
    public long Epoch { get; private set; } = 1;
    public int ContractVersion { get; set; } = CubaseConstants.DriverContractVersion;
    public TimeSpan? CallbackDelay { get; set; } = TimeSpan.Zero;
    public bool LoseReplyAfterApply { get; set; }
    public bool Offline { get; set; }
    public Action<string, JsonObject>? BeforeWrite { get; set; }
    public List<(string Operation, JsonObject Arguments)> Writes { get; } = [];
    public List<string> Reads { get; } = [];
    public Func<string, JsonObject, DriverReply?>? Override { get; set; }
    public Dictionary<string, Func<JsonObject, FakeDriver, bool>> Commands { get; } = new(StringComparer.Ordinal);

    public FakeDriver()
    {
        foreach (var key in State.Select(pair => pair.Key).ToArray()) _stateRevision[key] = 1;
    }

    public int WriteCount(string operation) { lock (_gate) return Writes.Count(item => item.Operation == operation); }

    public FakeChannel AddChannel(string title, int? bankIndex = null)
    {
        lock (_gate)
        {
            var index = bankIndex ?? (Channels.Count == 0 ? 0 : Channels.Max(channel => channel.BankIndex) + 1);
            var channel = new FakeChannel("uid-" + Guid.NewGuid().ToString("N")[..8], 500 + Channels.Count * 7, title, index);
            Channels.Add(channel);
            return channel;
        }
    }

    public void Select(FakeChannel channel) => SelectedUniqueId = channel.UniqueId;

    /// <summary>Simulates a driver reload: new session and epoch, unknown mirror.</summary>
    public void Reconnect()
    {
        lock (_gate)
        {
            SessionId++;
            Epoch++;
            foreach (var key in State.Select(pair => pair.Key).ToArray()) _stateRevision[key] = 0;
        }
    }

    public void HostChange(string key, JsonNode? value)
    {
        lock (_gate)
        {
            State[key] = value?.DeepClone();
            _revision++;
            _stateRevision[key] = _revision;
        }
    }

    private void Callback(string key, JsonNode? value)
    {
        if (CallbackDelay is null) return;
        if (CallbackDelay == TimeSpan.Zero) { HostChange(key, value); return; }
        var clone = value?.DeepClone();
        _ = Task.Delay(CallbackDelay.Value).ContinueWith(_ => HostChange(key, clone), TaskScheduler.Default);
    }

    public Task<DriverReply> SendAsync(string operation, JsonObject? arguments, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var args = arguments ?? new JsonObject();
        if (Offline) return Task.FromResult(new DriverReply(false, null, new BrokerError(ErrorCodes.DriverOffline, "offline"), false, _revision, false, Epoch));
        if (Override?.Invoke(operation, args) is { } overridden) return Task.FromResult(overridden);
        var isWrite = MidiOperations.IsWrite(operation);
        if (isWrite && ContractVersion != CubaseConstants.DriverContractVersion)
            return Task.FromResult(new DriverReply(false, null, new BrokerError(ErrorCodes.DriverContractMismatch, "old driver"), false, _revision, false, Epoch));
        try
        {
            JsonNode data;
            lock (_gate)
            {
                if (isWrite)
                {
                    BeforeWrite?.Invoke(operation, args);
                    Writes.Add((operation, (JsonObject)args.DeepClone()));
                }
                else Reads.Add(operation);
                data = Dispatch(operation, args);
            }
            if (isWrite && LoseReplyAfterApply)
                return Task.FromResult(new DriverReply(false, null, new BrokerError(ErrorCodes.OutcomeUnknown, "reply lost after apply"), false, _revision, true, Epoch));
            return Task.FromResult(new DriverReply(true, data, null, false, _revision, true, Epoch));
        }
        catch (FakeHostError error)
        {
            return Task.FromResult(new DriverReply(false, null, new BrokerError(error.Code, error.Message), false, _revision, true, Epoch));
        }
    }

    private sealed class FakeHostError(string code, string message) : Exception(message) { public string Code { get; } = code; }

    private JsonObject Identity(FakeChannel channel) => new()
    {
        ["bank_index"] = channel.BankIndex, ["unique_id"] = channel.UniqueId, ["runtime_id"] = channel.RuntimeId,
        ["title"] = channel.Title, ["object_id"] = 10_000 + channel.BankIndex * 10, ["kind"] = "mixer_channel"
    };

    private JsonObject SelectedJson()
    {
        var channel = Channels.FirstOrDefault(item => item.UniqueId == SelectedUniqueId);
        return new JsonObject
        {
            ["kind"] = "selected_track", ["unique_id"] = channel?.UniqueId, ["runtime_id"] = channel?.RuntimeId, ["title"] = channel?.Title
        };
    }

    private JsonObject Snapshot() => new()
    {
        ["state"] = State.DeepClone(),
        ["state_revision"] = new JsonObject(_stateRevision.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value)))),
        ["time_formats"] = new JsonObject(TimeFormats.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value)))),
        ["session_id"] = SessionId, ["revision"] = _revision
    };

    private FakeChannel Resolve(JsonNode? expected)
    {
        var uid = expected?["unique_id"]?.GetValue<string>() ?? throw new FakeHostError("TARGET_REQUIRED", "target required");
        var runtime = expected?["runtime_id"]?.GetValue<long?>();
        var matches = Channels.Where(channel => channel.UniqueId == uid).ToArray();
        if (matches.Length != 1) throw new FakeHostError("STALE_TARGET", "channel gone");
        if (runtime is not null && matches[0].RuntimeId != runtime) throw new FakeHostError("STALE_TARGET", "object replaced");
        return matches[0];
    }

    private void AssertSelected(JsonNode? expected)
    {
        var selected = Channels.FirstOrDefault(item => item.UniqueId == SelectedUniqueId);
        if (selected is null || selected.UniqueId != expected?["unique_id"]?.GetValue<string>()
            || (expected?["runtime_id"] is { } rid && rid.GetValue<long>() != selected.RuntimeId))
            throw new FakeHostError(ErrorCodes.TargetChanged, "selection changed");
    }

    private static string ParameterName(JsonObject args)
    {
        if (args["parameter"]?.GetValue<string>() is { } name) return name;
        return (args["parameter_tag"]?.GetValue<int>() ?? 0) switch { 0 => "volume", 1 => "pan", 2 => "mute", _ => "tag" };
    }

    private JsonNode Receipt(string action, bool sent, JsonObject extra)
    {
        var receipt = new JsonObject { ["action"] = action, ["mutation_sent"] = sent, ["no_op"] = !sent, ["dispatch_revision"] = _revision, ["session_id"] = SessionId };
        foreach (var pair in extra) receipt[pair.Key] = pair.Value?.DeepClone();
        return receipt;
    }

    private JsonNode Toggle(string action, string key, bool desired)
    {
        var before = State[key];
        if (before is null || _stateRevision[key] == 0) throw new FakeHostError(ErrorCodes.StateUnknown, "state unknown");
        if (before.GetValue<bool>() == desired) return Receipt(action, false, new JsonObject { ["state_key"] = key, ["before"] = desired, ["desired"] = desired });
        _revision++;
        var dispatch = _revision;
        Callback(key, desired);
        return Receipt(action, true, new JsonObject { ["state_key"] = key, ["before"] = !desired, ["desired"] = desired, ["dispatch_revision"] = dispatch });
    }

    private JsonNode Dispatch(string operation, JsonObject args)
    {
        switch (operation)
        {
            case "transport.get":
            case "status.get":
                return Snapshot();
            case "transport.set":
                var action = args["action"]!.GetValue<string>();
                switch (action)
                {
                    case "play": return Toggle(action, "playing", true);
                    case "record_start": return Toggle(action, "recording", true);
                    case "record_stop": return Toggle(action, "recording", false);
                    case "record": return Toggle(args["enabled"]?.GetValue<bool>() == false ? "record_stop" : "record_start", "recording", args["enabled"]?.GetValue<bool>() != false);
                    case "cycle": return Toggle(action, "cycle", args["enabled"]!.GetValue<bool>());
                    case "metronome": return Toggle(action, "metronome", args["enabled"]!.GetValue<bool>());
                    case "stop":
                        if (!State["playing"]!.GetValue<bool>() && !State["recording"]!.GetValue<bool>())
                            return Receipt("stop", false, new JsonObject { ["state_key"] = "playing", ["desired"] = false });
                        _revision++;
                        var stopRevision = _revision;
                        Callback("recording", false);
                        Callback("playing", false);
                        return Receipt("stop", true, new JsonObject { ["state_key"] = "playing", ["desired"] = false, ["dispatch_revision"] = stopRevision });
                    case "tempo":
                        var bpm = args["bpm"]!.GetValue<double>();
                        if (Math.Abs(State["tempo"]!.GetValue<double>() - bpm) <= 0.005)
                            return Receipt("tempo", false, new JsonObject { ["state_key"] = "tempo", ["desired"] = bpm });
                        _revision++;
                        var tempoRevision = _revision;
                        Callback("tempo", bpm);
                        return Receipt("tempo", true, new JsonObject { ["state_key"] = "tempo", ["desired"] = bpm, ["dispatch_revision"] = tempoRevision });
                    case "position":
                    case "left_locator":
                    case "right_locator":
                        var key = action == "position" ? "position" : action == "left_locator" ? "leftLocator" : "rightLocator";
                        if (args["time_format"]?.GetValue<string>() != TimeFormats[key])
                            throw new FakeHostError(ErrorCodes.UnsupportedTimeFormat, "time format mismatch");
                        // Like the driver, only identical text is a no-op; like Cubase, a time that does not move reports no change.
                        var currentTime = State[key]?.GetValue<string>();
                        var desiredTime = args["position_text"]!.GetValue<string>();
                        if (currentTime == desiredTime)
                            return Receipt(action, false, new JsonObject { ["state_key"] = key, ["before"] = currentTime, ["desired"] = desiredTime, ["time_format"] = TimeFormats[key] });
                        _revision++;
                        var positionRevision = _revision;
                        if (currentTime is null || PostconditionVerifier.CanonicalTimeText(currentTime) != PostconditionVerifier.CanonicalTimeText(desiredTime))
                            Callback(key, desiredTime);
                        return Receipt(action, true, new JsonObject
                        {
                            ["state_key"] = key, ["before"] = currentTime, ["desired"] = args["position_text"]!.DeepClone(),
                            ["time_format"] = TimeFormats[key], ["dispatch_revision"] = positionRevision
                        });
                }
                throw new FakeHostError(ErrorCodes.InvalidAction, action);
            case "track.list":
                return new JsonObject
                {
                    ["scope"] = "mixer_bank", ["bank_size"] = 64,
                    ["channels"] = new JsonArray(Channels.Where(channel => channel.BankIndex is >= 0 and < 64)
                        .OrderBy(channel => channel.BankIndex).Select(channel => (JsonNode?)Identity(channel)).ToArray()),
                    ["bank_truncated"] = Channels.Count(channel => channel.BankIndex is >= 0 and < 64) >= 64,
                    ["selected"] = SelectedJson(), ["session_id"] = SessionId
                };
            case "track.get":
                return new JsonObject { ["selected"] = SelectedJson() };
            case "track.set":
                var ordered = Channels.OrderBy(channel => channel.BankIndex).ToList();
                var index = ordered.FindIndex(channel => channel.UniqueId == SelectedUniqueId);
                var next = args["action"]?.GetValue<string>() == "next" ? index + 1 : index - 1;
                if (next >= 0 && next < ordered.Count) SelectedUniqueId = ordered[next].UniqueId;
                return Receipt(args["action"]!.GetValue<string>(), true, new JsonObject());
            case "mixer.get":
                if (args["target"] is { } readTarget)
                {
                    var channel = Resolve(readTarget);
                    var parameter = ParameterName(args);
                    return new JsonObject
                    {
                        ["target"] = Identity(channel), ["parameter_tag"] = args["parameter_tag"]?.DeepClone() ?? 0,
                        ["process_value"] = channel.Parameters.GetValueOrDefault(parameter), ["display_value"] = channel.Parameters.GetValueOrDefault(parameter).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                        ["fresh"] = true
                    };
                }
                var selected = Channels.FirstOrDefault(item => item.UniqueId == SelectedUniqueId);
                var mirror = new JsonObject();
                if (selected is not null) foreach (var pair in selected.Parameters) mirror[pair.Key] = pair.Key is "volume" or "pan" ? pair.Value : pair.Value >= 0.5;
                return new JsonObject { ["selected"] = SelectedJson(), ["state"] = mirror, ["state_revision"] = new JsonObject(mirror.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(_revision)))) };
            case "mixer.set":
            case "direct.set":
            case "plugin.set" when args["action"]?.GetValue<string>() == "set_parameter":
                if (args["track_id"] is not null) throw new FakeHostError(ErrorCodes.LegacyTargetAmbiguous, "legacy");
                FakeChannel target;
                if (args["target"] is { } expected) target = Resolve(expected);
                else
                {
                    AssertSelected(args["expected_selected"] ?? throw new FakeHostError(ErrorCodes.TargetRequired, "target required"));
                    target = Channels.First(item => item.UniqueId == SelectedUniqueId);
                }
                if (target.Locked) throw new FakeHostError(ErrorCodes.ParameterLocked, "locked");
                if (target.DuplicateVolumeTitles > 1 && args["parameter"]?.GetValue<string>() == "volume")
                    throw new FakeHostError(ErrorCodes.ParameterAmbiguous, "ambiguous");
                var name = ParameterName(args);
                var requested = args["process_value"]?.GetValue<double>()
                    ?? (args["value"] is JsonValue raw ? raw.TryGetValue<bool>(out var flag) ? flag ? 1 : 0 : raw.GetValue<double>() : 0);
                var beforeValue = target.Parameters.GetValueOrDefault(name);
                if (Math.Abs(beforeValue - requested) <= 1e-6)
                    return Receipt("set", false, new JsonObject { ["before"] = beforeValue, ["after"] = beforeValue, ["requested_process_value"] = requested, ["parameter_tag"] = 0, ["target"] = Identity(target) });
                _revision++;
                var dispatchRevision = _revision;
                if (CallbackDelay is not null) target.Parameters[name] = requested;
                return Receipt("set", true, new JsonObject
                {
                    ["before"] = beforeValue, ["after"] = target.Parameters[name], ["requested_process_value"] = requested,
                    ["parameter_tag"] = args["parameter_tag"]?.DeepClone() ?? (name == "pan" ? 1 : name == "mute" ? 2 : 0),
                    ["object_id"] = args["object_id"]?.DeepClone(),
                    ["target"] = Identity(target), ["dispatch_revision"] = dispatchRevision, ["state_key"] = name, ["desired"] = requested
                });
            case "direct.get":
                var directTarget = args["target"] is { } dt ? Resolve(dt) : Channels.First(item => item.UniqueId == SelectedUniqueId);
                var directName = ParameterName(args);
                return new JsonObject { ["target"] = Identity(directTarget), ["read"] = new JsonObject { ["process_value"] = directTarget.Parameters.GetValueOrDefault(directName) }, ["fresh"] = true };
            case "plugin.get" when args["action"]?.GetValue<string>() == "catalog":
                if (DriverPluginCatalog is null) return new JsonObject { ["available"] = false, ["reason"] = ErrorCodes.PluginCatalogUnavailable, ["entries"] = new JsonArray() };
                var offset = args["offset"]?.GetValue<int>() ?? 0;
                var limit = args["limit"]?.GetValue<int>() ?? 100;
                return new JsonObject
                {
                    ["available"] = true, ["total"] = DriverPluginCatalog.Count, ["offset"] = offset, ["slot_object_id"] = args["object_id"]?.DeepClone() ?? 7,
                    ["entries"] = new JsonArray(DriverPluginCatalog.Skip(offset).Take(limit).Select(entry => (JsonNode?)entry.DeepClone()).ToArray())
                };
            case "plugin.get":
                return new JsonObject { ["slot"] = new JsonObject { ["object_id"] = args["object_id"]?.DeepClone(), ["unique_id"] = SlotUid, ["title"] = SlotTitle,
                    ["loaded"] = SlotLoadedUid is null ? null : new JsonObject { ["plugin_uid"] = SlotLoadedUid, ["unique_name"] = SlotLoadedUid + "-1" } } };
            case "plugin.set":
                if (args["expected_slot_unique_id"]?.GetValue<string>() is { } expectedSlot && expectedSlot != SlotUid)
                    throw new FakeHostError(ErrorCodes.TargetChanged, "slot changed");
                SlotTitle = "Assigned";
                if (AssignAccepted && DriverPluginCatalog is not null)
                    SlotLoadedUid = LoadedUidOverride ?? new string(args["plugin_uid"]!.GetValue<string>().Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
                return Receipt("assign", true, new JsonObject { ["accepted"] = AssignAccepted });
            case "command.execute":
                if (args["expected_selected"] is { } guard) AssertSelected(guard);
                else if (args["requires_target"]?.GetValue<bool>() == true) throw new FakeHostError(ErrorCodes.TargetContextUnsafe, "unguarded");
                var id = args["id"]!.GetValue<string>();
                if (Commands.TryGetValue(id, out var handler) && !handler(args, this)) throw new FakeHostError("COMMAND_NOT_PERFORMABLE", "refused");
                _revision++;
                return Receipt("execute", true, new JsonObject { ["id"] = id });
            case "project.get":
                return new JsonObject { ["state"] = State.DeepClone() };
            default:
                throw new FakeHostError("UNSUPPORTED_CAPABILITY", operation);
        }
    }

    public string SlotUid { get; set; } = "slot-empty";
    public string SlotTitle { get; set; } = "Empty";
    public bool AssignAccepted { get; set; } = true;
    /// <summary>API 1.3 plug-in catalog served by the driver itself; null models a host without it.</summary>
    public List<JsonObject>? DriverPluginCatalog { get; set; }
    /// <summary>Class UID named by the slot's child object (null: empty slot).</summary>
    public string? SlotLoadedUid { get; set; }
    /// <summary>When set, the host loads this UID instead of the requested one.</summary>
    public string? LoadedUidOverride { get; set; }

    public Task<bool> TryHandshakeAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(!Offline);
    public long Revision => _revision;
    public JsonObject EventState => State;
    public DriverHelloInfo? Hello => new(ContractVersion, "fake-build", "15.0.20",
        new JsonObject { ["api_1_3_plugin_manager"] = DriverPluginCatalog is not null, ["plugin_catalog"] = DriverPluginCatalog is not null }, SessionId, Epoch);
    public LoopMidiRuntimeSnapshot? LoopMidiSnapshot => null;
    public long StaleFramesDiscarded => 0;
    public string? LastTransportError => null;
    public IReadOnlyList<MidiPortSnapshot> EnumeratePorts() => [];
}

public sealed class FakeProcess : IHostProcessProbe
{
    public string Title { get; set; } = "Cubase Pro Project - Fixture";
    public bool Running { get; set; } = true;
    public CubaseProcessState Inspect() => Running
        ? new CubaseProcessState(true, 4242, "15.0.20", Title, IntPtr.Zero, new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero))
        : new CubaseProcessState(false, null, null, null, IntPtr.Zero, null);
}

public sealed class FakePluginCatalog(params PluginCatalogEntry[] entries) : IPluginCatalogProvider
{
    public string? LoadedUid { get; set; }
    public TimeSpan LoadDelay { get; set; }
    private DateTimeOffset _assignedAt = DateTimeOffset.MaxValue;
    public void MarkAssigned() => _assignedAt = DateTimeOffset.UtcNow;
    public Task<PluginCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PluginCatalogSnapshot(true, "fake", entries, null));
    public Task<string?> ObserveSlotPluginUidAsync(TargetIdentity target, long slotObjectId, CancellationToken cancellationToken) =>
        Task.FromResult(DateTimeOffset.UtcNow - _assignedAt >= LoadDelay ? LoadedUid : null);
}
