using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

/// <summary>An outstanding MIDI request. Only a frame matching all of these fields may complete it.</summary>
public sealed record PendingDriverRequest(
    uint RequestId,
    uint ExpectedSessionId,
    long DriverEpoch,
    ushort Opcode,
    string Operation,
    string? HelloNonce,
    string PayloadHash,
    DateTimeOffset SentAt,
    DateTimeOffset Deadline);

public enum FrameDisposition
{
    /// <summary>A current-session state event; apply it to the mirror.</summary>
    AcceptEvent,
    /// <summary>A terminal frame that completes the matching pending request.</summary>
    CompletePending,
    /// <summary>A hello reply whose nonce and session match; adopt it as the new epoch.</summary>
    AdoptHello,
    /// <summary>Ignored: stale session, unsolicited, mismatched opcode or nonce. Nothing is updated.</summary>
    Discard
}

public sealed record FrameDecision(FrameDisposition Disposition, PendingDriverRequest? Pending, string Reason);

/// <summary>
/// Receive-side rules for driver frames, kept free of I/O so they can be tested deterministically.
/// Order: (1) the caller has already checked CRC/length/chunk bounds; (2) session/epoch match; (3) events are
/// accepted only from the current session; (4) terminal frames must match a pending request by request ID,
/// opcode and session; (5) only accepted frames may update revision, liveness or state.
/// A hello reply must echo the pending nonce and carry the same session ID in its body and frame header.
/// </summary>
public sealed class DriverFrameFilter
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, PendingDriverRequest> _pending = new();

    public uint CurrentSessionId { get; private set; }
    public long Epoch { get; private set; }
    public long StaleFramesDiscarded { get; private set; }
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public void Register(PendingDriverRequest request)
    {
        lock (_gate)
        {
            if (!_pending.TryAdd(request.RequestId, request))
                throw new InvalidOperationException("Duplicate MIDI request ID allocation.");
        }
    }

    public void Remove(uint requestId)
    {
        lock (_gate) _pending.Remove(requestId);
    }

    public FrameDecision Evaluate(MidiFrame frame, JsonObject envelope)
    {
        lock (_gate)
        {
            if (frame.Kind == MidiFrameKind.Event)
            {
                if (CurrentSessionId != 0 && frame.SessionId == CurrentSessionId)
                    return new FrameDecision(FrameDisposition.AcceptEvent, null, "current_session_event");
                StaleFramesDiscarded++;
                return new FrameDecision(FrameDisposition.Discard, null, "event_from_other_session");
            }

            if (!_pending.TryGetValue(frame.RequestId, out var pending))
            {
                StaleFramesDiscarded++;
                return new FrameDecision(FrameDisposition.Discard, null, "no_pending_request");
            }
            if (pending.Opcode != frame.Opcode)
            {
                StaleFramesDiscarded++;
                return new FrameDecision(FrameDisposition.Discard, null, "opcode_mismatch");
            }

            if (pending.HelloNonce is not null)
            {
                var data = envelope["data"] as JsonObject;
                var bodySession = TryUInt(data?["session_id"]);
                var nonce = data?["client_nonce"]?.GetValue<string>();
                if (envelope["ok"]?.GetValue<bool>() == true && bodySession is > 0 && bodySession == frame.SessionId
                    && string.Equals(nonce, pending.HelloNonce, StringComparison.Ordinal))
                    return new FrameDecision(FrameDisposition.AdoptHello, pending, "hello_nonce_and_session_match");
                StaleFramesDiscarded++;
                return new FrameDecision(FrameDisposition.Discard, null, "hello_nonce_or_session_mismatch");
            }

            if (pending.DriverEpoch != Epoch)
            {
                StaleFramesDiscarded++;
                return new FrameDecision(FrameDisposition.Discard, null, "pending_from_old_epoch");
            }
            if (frame.SessionId == pending.ExpectedSessionId)
                return new FrameDecision(FrameDisposition.CompletePending, pending, "request_session_match");
            // The driver answers a request for an old session with STALE_SESSION in its own (new) session header.
            // That refusal is authoritative that the request was not dispatched, so it may complete the request.
            if (frame.Kind == MidiFrameKind.Nack
                && (envelope["error"] as JsonObject)?["code"]?.GetValue<string>() == "STALE_SESSION")
                return new FrameDecision(FrameDisposition.CompletePending, pending, "stale_session_refusal");
            StaleFramesDiscarded++;
            return new FrameDecision(FrameDisposition.Discard, null, "response_session_mismatch");
        }
    }

    /// <summary>Starts a new epoch for an accepted hello and returns the pending requests of older epochs.</summary>
    public IReadOnlyList<PendingDriverRequest> AdoptSession(uint sessionId)
    {
        lock (_gate)
        {
            CurrentSessionId = sessionId;
            Epoch++;
            var orphaned = _pending.Values.Where(item => item.HelloNonce is null && item.DriverEpoch != Epoch).ToArray();
            foreach (var item in orphaned) _pending.Remove(item.RequestId);
            return orphaned;
        }
    }

    /// <summary>Marks the driver session as lost (e.g. STALE_SESSION). The next request performs a new hello.</summary>
    public void ForgetSession()
    {
        lock (_gate) CurrentSessionId = 0;
    }

    private static uint? TryUInt(JsonNode? node) =>
        JsonNumbers.ReadLong(node) is { } value && value is >= 0 and <= uint.MaxValue ? (uint)value : null;
}
