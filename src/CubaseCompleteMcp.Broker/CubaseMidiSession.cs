using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using Microsoft.Extensions.Logging;

namespace CubaseCompleteMcp.Broker;

public sealed record DriverReply(bool Ok, JsonNode? Data, BrokerError? Error, bool Verified, long Revision);

public sealed class CubaseMidiSession
{
    private readonly WinMmMidiTransport _transport;
    private readonly ILogger<CubaseMidiSession> _logger;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DriverReply>> _pending = new();
    private readonly MidiFrameReassembler _reassembler = new();
    private readonly SemaphoreSlim _helloGate = new(1, 1);
    private readonly object _stateGate = new();
    private uint _sessionId;
    private int _requestCounter;
    private int _sequenceCounter;
    private DateTimeOffset _lastReplyAt;
    private JsonObject _eventState = new();

    public CubaseMidiSession(WinMmMidiTransport transport, ILogger<CubaseMidiSession> logger)
    {
        _transport = transport;
        _logger = logger;
        _transport.MessageReceived += OnMessageReceived;
    }

    public bool IsTransportOpen => _transport.IsOpen;
    public bool IsDriverOnline => _sessionId != 0 && DateTimeOffset.UtcNow - _lastReplyAt < TimeSpan.FromSeconds(15);
    public uint SessionId => _sessionId;
    public long Revision { get; private set; }

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

    public bool EnsureTransportOpen() => _transport.TryOpen(
        CubaseConstants.OutputPortName,
        CubaseConstants.InputPortName);

    public async Task<bool> TryHandshakeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!EnsureTransportOpen())
        {
            return false;
        }

        if (IsDriverOnline)
        {
            return true;
        }

        await _helloGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDriverOnline)
            {
                return true;
            }

            _sessionId = 0;
            var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var arguments = new JsonObject
            {
                ["client_nonce"] = nonce,
                ["min_protocol_version"] = 2,
                ["max_protocol_version"] = 2
            };
            var reply = await SendInternalAsync("system.hello", arguments, 0, timeout, cancellationToken).ConfigureAwait(false);
            if (!reply.Ok)
            {
                return false;
            }

            var session = reply.Data?["session_id"]?.GetValue<uint>() ?? 0;
            var echoedNonce = reply.Data?["client_nonce"]?.GetValue<string>();
            if (session == 0 || !string.Equals(echoedNonce, nonce, StringComparison.Ordinal))
            {
                _logger.LogWarning("Cubase driver hello response did not contain a valid session ID and nonce.");
                return false;
            }

            _sessionId = session;
            _lastReplyAt = DateTimeOffset.UtcNow;
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidDataException)
        {
            _logger.LogDebug(error, "Cubase driver handshake did not complete.");
            _sessionId = 0;
            return false;
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
                new BrokerError("DRIVER_OFFLINE", "The Cubase MIDI Remote v2 driver did not answer the handshake."),
                false, Revision);
        }

        var reply = await SendInternalAsync(operation, arguments ?? new JsonObject(), _sessionId,
            timeout ?? TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        if (reply.Error?.Code == "STALE_SESSION")
        {
            _sessionId = 0;
        }

        return reply;
    }

    private async Task<DriverReply> SendInternalAsync(string operation, JsonObject arguments, uint sessionId,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_pending.Count >= 128)
        {
            return new DriverReply(false, null,
                new BrokerError("BACKPRESSURE", "Too many Cubase MIDI requests are already in flight."), false, Revision);
        }
        var requestId = unchecked((uint)Interlocked.Increment(ref _requestCounter));
        if (requestId == 0)
        {
            requestId = unchecked((uint)Interlocked.Increment(ref _requestCounter));
        }

        var sequence = unchecked((uint)Interlocked.Add(ref _sequenceCounter, 32));
        var opcode = MidiOperations.GetOpcode(operation);
        var payload = new JsonObject
        {
            ["operation"] = operation,
            ["arguments"] = arguments.DeepClone()
        }.ToJsonString(PipeProtocol.JsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var completion = new TaskCompletionSource<DriverReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("Duplicate MIDI request ID allocation.");
        }

        try
        {
            var messages = MidiProtocolV2.Encode(MidiFrameKind.Request, sessionId, requestId, sequence, opcode, bytes);
            var retryDelay = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(100).Ticks, timeout.Ticks / 3));
            for (var attempt = 0; attempt < 3; attempt++)
            {
                foreach (var message in messages)
                {
                    await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
                }

                var delay = Task.Delay(retryDelay, cancellationToken);
                if (await Task.WhenAny(completion.Task, delay).ConfigureAwait(false) == completion.Task)
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            throw new TimeoutException($"Cubase driver did not answer '{operation}' after three transmissions.");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private void OnMessageReceived(object? sender, byte[] message)
    {
        try
        {
            var chunk = MidiProtocolV2.DecodeChunk(message);
            var frame = _reassembler.Add(chunk);
            if (frame is null)
            {
                return;
            }

            var envelope = JsonNode.Parse(frame.Payload) as JsonObject
                ?? throw new InvalidDataException("Cubase driver response is not a JSON object.");
            var revision = envelope["revision"]?.GetValue<long>() ?? Revision;
            Revision = Math.Max(Revision, revision);
            _lastReplyAt = DateTimeOffset.UtcNow;

            if (frame.Kind == MidiFrameKind.Event)
            {
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
            }

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

            var reply = new DriverReply(ok, envelope["data"]?.DeepClone(), error, verified, revision);
            if (_pending.TryGetValue(frame.RequestId, out var completion))
            {
                completion.TrySetResult(reply);
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Rejected an invalid Cubase MIDI response.");
        }
    }
}
