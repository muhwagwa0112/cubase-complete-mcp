using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using Microsoft.Extensions.Logging;

namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Result of one driver request. <see cref="Sent"/> is true once the request may have reached Cubase; a write
/// with <c>Sent</c> and no reply is an unknown outcome and is never reissued under a new request or session.
/// </summary>
public sealed record DriverReply(bool Ok, JsonNode? Data, BrokerError? Error, bool Verified, long Revision,
    bool Sent = true, long Epoch = 0);

public sealed record DriverHelloInfo(int? ContractVersion, string? BuildId, string? HostVersion, JsonNode? Capabilities,
    uint SessionId, long Epoch);

/// <summary>Driver access used by the controller; the production implementation is <see cref="CubaseMidiSession"/>.</summary>
public interface ICubaseDriver
{
    Task<DriverReply> SendAsync(string operation, JsonObject? arguments, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
    Task<bool> TryHandshakeAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    long Revision { get; }
    long Epoch { get; }
    JsonObject EventState { get; }
    DriverHelloInfo? Hello { get; }
    LoopMidiRuntimeSnapshot? LoopMidiSnapshot { get; }
    long StaleFramesDiscarded { get; }
    string? LastTransportError { get; }
    IReadOnlyList<MidiPortSnapshot> EnumeratePorts();
}

public sealed class CubaseMidiSession : ICubaseDriver
{
    private readonly WinMmMidiTransport _transport;
    private readonly LoopMidiRuntime _loopMidi;
    private readonly ILogger<CubaseMidiSession> _logger;
    private readonly DriverFrameFilter _filter = new();
    private readonly Dictionary<uint, TaskCompletionSource<DriverReply>> _completions = new();
    private readonly MidiFrameReassembler _reassembler = new();
    private readonly SemaphoreSlim _helloGate = new(1, 1);
    private readonly object _stateGate = new();
    private int _requestCounter;
    private int _sequenceCounter;
    private DateTimeOffset _lastReplyAt;
    private JsonObject _eventState = new();
    private long _revision;

    public CubaseMidiSession(WinMmMidiTransport transport, LoopMidiRuntime loopMidi,
        ILogger<CubaseMidiSession> logger)
    {
        _transport = transport;
        _loopMidi = loopMidi;
        _logger = logger;
        _transport.MessageReceived += OnMessageReceived;
    }

    public bool IsTransportOpen => _transport.IsOpen;
    public bool IsDriverOnline => _filter.CurrentSessionId != 0 && DateTimeOffset.UtcNow - _lastReplyAt < TimeSpan.FromSeconds(15);
    public uint SessionId => _filter.CurrentSessionId;
    public long Epoch => _filter.Epoch;
    public long Revision => Interlocked.Read(ref _revision);
    public long StaleFramesDiscarded => _filter.StaleFramesDiscarded;
    public DriverHelloInfo? Hello { get; private set; }
    public LoopMidiRuntimeSnapshot? LoopMidiSnapshot { get; private set; }
    public string? LastTransportError => _transport.LastError;
    public IReadOnlyList<MidiPortSnapshot> EnumeratePorts() => _transport.EnumeratePorts();

    public JsonObject EventState
    {
        get
        {
            lock (_stateGate)
            {
                return (JsonObject)_eventState.DeepClone();
            }
        }
    }

    public async Task<bool> EnsureTransportOpenAsync(CancellationToken cancellationToken = default)
    {
        LoopMidiSnapshot = await _loopMidi.EnsureAvailableAsync(_transport.EnumeratePorts, cancellationToken)
            .ConfigureAwait(false);
        return LoopMidiSnapshot.PortsAvailable && _transport.TryOpen(
            CubaseConstants.OutputPortName, CubaseConstants.InputPortName);
    }

    public async Task<bool> TryHandshakeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!await EnsureTransportOpenAsync(cancellationToken).ConfigureAwait(false)) return false;
        if (IsDriverOnline) return true;

        await _helloGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDriverOnline) return true;
            _filter.ForgetSession();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var arguments = new JsonObject
            {
                ["client_nonce"] = nonce,
                ["min_protocol_version"] = CubaseConstants.MidiWireProtocolVersion,
                ["max_protocol_version"] = CubaseConstants.MidiWireProtocolVersion,
                ["driver_contract_version"] = CubaseConstants.DriverContractVersion
            };
            var reply = await SendInternalAsync("system.hello", arguments, 0, nonce, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!reply.Ok)
            {
                _logger.LogDebug("Cubase driver hello was not accepted: {Code}", reply.Error?.Code);
                return false;
            }
            // Session adoption already happened inside the receive filter (nonce + session verified).
            _lastReplyAt = DateTimeOffset.UtcNow;
            return _filter.CurrentSessionId != 0;
        }
        finally
        {
            _helloGate.Release();
        }
    }

    public async Task<DriverReply> SendAsync(string operation, JsonObject? arguments,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!await TryHandshakeAsync(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false))
        {
            return new DriverReply(false, null,
                new BrokerError(ErrorCodes.DriverOffline, "The Cubase MIDI Remote v2 driver did not answer the handshake."),
                false, Revision, Sent: false, Epoch: Epoch);
        }
        if (MidiOperations.IsWrite(operation) && Hello?.ContractVersion != CubaseConstants.DriverContractVersion)
        {
            return new DriverReply(false, null,
                new BrokerError(ErrorCodes.DriverContractMismatch,
                    $"The loaded CompleteBridge driver reports contract {Hello?.ContractVersion?.ToString() ?? "none"}; contract {CubaseConstants.DriverContractVersion} is required before any write. Regenerate and reload the driver."),
                false, Revision, Sent: false, Epoch: Epoch);
        }

        var reply = await SendInternalAsync(operation, arguments ?? new JsonObject(), _filter.CurrentSessionId, null,
            timeout ?? TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        if (reply.Error?.Code == "STALE_SESSION") _filter.ForgetSession();
        return reply;
    }

    private async Task<DriverReply> SendInternalAsync(string operation, JsonObject arguments, uint sessionId,
        string? helloNonce, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var isWrite = MidiOperations.IsWrite(operation);
        if (_filter.PendingCount >= 128)
            return new DriverReply(false, null,
                new BrokerError("BACKPRESSURE", "Too many Cubase MIDI requests are already in flight."), false, Revision,
                Sent: false, Epoch: Epoch);
        var requestId = unchecked((uint)Interlocked.Increment(ref _requestCounter));
        if (requestId == 0) requestId = unchecked((uint)Interlocked.Increment(ref _requestCounter));

        var sequence = unchecked((uint)Interlocked.Add(ref _sequenceCounter, 32));
        var opcode = MidiOperations.GetOpcode(operation);
        var payload = new JsonObject
        {
            ["operation"] = operation,
            ["arguments"] = arguments.DeepClone()
        }.ToJsonString(PipeProtocol.JsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var epochAtSend = Epoch;
        var completion = new TaskCompletionSource<DriverReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;
        lock (_completions) _completions[requestId] = completion;
        _filter.Register(new PendingDriverRequest(requestId, sessionId, epochAtSend, opcode, operation, helloNonce,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), now, now + timeout));

        var sent = false;
        try
        {
            var messages = MidiProtocolV2.Encode(MidiFrameKind.Request, sessionId, requestId, sequence, opcode, bytes);
            var retryDelay = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(100).Ticks, timeout.Ticks / 3));
            // Same request ID, same payload, same session only: the driver's request cache answers a duplicate
            // without re-applying it. A session change stops retransmission; nothing is reissued as a new request.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0 && helloNonce is null && Epoch != epochAtSend) break;
                foreach (var message in messages)
                {
                    sent = true;
                    await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
                }

                var delay = Task.Delay(retryDelay, cancellationToken);
                if (await Task.WhenAny(completion.Task, delay).ConfigureAwait(false) == completion.Task)
                    return await completion.Task.ConfigureAwait(false);
            }
            return NoReply(operation, isWrite, sent, "did not answer after bounded same-session retransmission");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!sent) throw;
            return NoReply(operation, isWrite, sent, "was cancelled while waiting for the driver");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return NoReply(operation, isWrite, sent, $"failed in the MIDI transport ({error.Message})");
        }
        finally
        {
            _filter.Remove(requestId);
            lock (_completions) _completions.Remove(requestId);
        }
    }

    private DriverReply NoReply(string operation, bool isWrite, bool sent, string reason)
    {
        if (isWrite && sent)
            return new DriverReply(false, null, new BrokerError(ErrorCodes.OutcomeUnknown,
                $"Cubase driver operation '{operation}' {reason}. The change may have been applied and was not re-sent."),
                false, Revision, Sent: true, Epoch: Epoch);
        return new DriverReply(false, null, new BrokerError(sent ? "DRIVER_TIMEOUT" : ErrorCodes.DriverOffline,
            $"Cubase driver operation '{operation}' {reason}."), false, Revision, Sent: sent, Epoch: Epoch);
    }

    private void OnMessageReceived(object? sender, byte[] message)
    {
        try
        {
            var chunk = MidiProtocolV2.DecodeChunk(message);
            // Frames for a session other than the current one (or a pending hello) are dropped before reassembly,
            // so a stale stream cannot occupy reassembly buffers or refresh liveness.
            if (chunk.Kind == MidiFrameKind.Event && chunk.SessionId != _filter.CurrentSessionId) { _ = _filter.Evaluate(chunk, new JsonObject()); return; }
            var frame = _reassembler.Add(chunk);
            if (frame is null) return;

            var envelope = JsonNode.Parse(frame.Payload) as JsonObject
                ?? throw new InvalidDataException("Cubase driver response is not a JSON object.");
            var decision = _filter.Evaluate(frame, envelope);
            switch (decision.Disposition)
            {
                case FrameDisposition.Discard:
                    return;
                case FrameDisposition.AdoptHello:
                    AdoptHello(frame, envelope);
                    Complete(decision.Pending!, frame, envelope);
                    return;
                case FrameDisposition.AcceptEvent:
                    ApplyRevision(envelope);
                    _lastReplyAt = DateTimeOffset.UtcNow;
                    var data = envelope["data"] as JsonObject;
                    var key = data?["key"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        lock (_stateGate)
                        {
                            _eventState[key] = data?["value"]?.DeepClone();
                        }
                    }
                    return;
                case FrameDisposition.CompletePending:
                    ApplyRevision(envelope);
                    _lastReplyAt = DateTimeOffset.UtcNow;
                    Complete(decision.Pending!, frame, envelope);
                    return;
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Rejected an invalid Cubase MIDI response.");
        }
    }

    private void AdoptHello(MidiFrame frame, JsonObject envelope)
    {
        var data = envelope["data"] as JsonObject;
        var orphaned = _filter.AdoptSession(frame.SessionId);
        lock (_stateGate)
        {
            // A new epoch starts with an unknown mirror; old-session values are never treated as current facts.
            _eventState = new JsonObject();
        }
        Interlocked.Exchange(ref _revision, envelope["revision"]?.GetValue<long>() ?? 0);
        Hello = new DriverHelloInfo(data?["driver_contract_version"]?.GetValue<int>(),
            data?["driver_build_id"]?.GetValue<string>(), data?["capabilities"]?["host_version"]?.GetValue<string>(),
            data?["capabilities"]?.DeepClone(), frame.SessionId, _filter.Epoch);
        foreach (var pending in orphaned)
        {
            TaskCompletionSource<DriverReply>? completion;
            lock (_completions) _completions.TryGetValue(pending.RequestId, out completion);
            completion?.TrySetResult(NoReply(pending.Operation, MidiOperations.IsWrite(pending.Operation), true,
                "was pending when the driver session changed"));
        }
    }

    private void ApplyRevision(JsonObject envelope)
    {
        // Revisions are only comparable within one epoch; a new epoch resets the baseline in AdoptHello.
        var revision = envelope["revision"]?.GetValue<long>();
        if (revision is null) return;
        long current;
        do { current = Interlocked.Read(ref _revision); }
        while (revision > current && Interlocked.CompareExchange(ref _revision, revision.Value, current) != current);
    }

    private void Complete(PendingDriverRequest pending, MidiFrame frame, JsonObject envelope)
    {
        var ok = envelope["ok"]?.GetValue<bool>() ?? frame.Kind is MidiFrameKind.Response or MidiFrameKind.Ack or MidiFrameKind.Heartbeat;
        var verified = envelope["verified"]?.GetValue<bool>() ?? false;
        BrokerError? error = null;
        if (!ok && envelope["error"] is JsonObject errorNode)
        {
            error = new BrokerError(
                errorNode["code"]?.GetValue<string>() ?? "DRIVER_ERROR",
                errorNode["message"]?.GetValue<string>() ?? "Cubase driver reported an error.",
                errorNode["details"]?.DeepClone());
        }
        var reply = new DriverReply(ok, envelope["data"]?.DeepClone(), error, verified,
            envelope["revision"]?.GetValue<long>() ?? Revision, Sent: true, Epoch: _filter.Epoch);
        TaskCompletionSource<DriverReply>? completion;
        lock (_completions) _completions.TryGetValue(pending.RequestId, out completion);
        completion?.TrySetResult(reply);
    }
}
