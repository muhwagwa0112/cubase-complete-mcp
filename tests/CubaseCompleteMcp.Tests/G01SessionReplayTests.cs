using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using CubaseCompleteMcp.Tests.Fakes;

namespace CubaseCompleteMcp.Tests;

/// <summary>Scripted broker pipe: records every request that fully arrived and answers according to its mode.</summary>
internal sealed class ScriptedBrokerStream(ScriptedBrokerStream.Mode mode, List<BrokerRequest> received) : Stream
{
    public enum Mode { Respond, FailRead, HangRead, WrongId }

    private readonly MemoryStream _inbound = new();
    private MemoryStream? _response;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _inbound.Write(buffer.Span);
        var bytes = _inbound.ToArray();
        if (bytes.Length >= 4)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes);
            if (bytes.Length >= 4 + length)
            {
                var request = JsonSerializer.Deserialize<BrokerRequest>(bytes.AsSpan(4, length), PipeProtocol.JsonOptions)!;
                lock (received) received.Add(request);
                _inbound.SetLength(0);
                var responseId = mode == Mode.WrongId ? "someone-else" : request.Id;
                var response = BrokerResponse.Success(responseId, new JsonObject { ["echo"] = request.Operation }, "scripted");
                var payload = JsonSerializer.SerializeToUtf8Bytes(response, PipeProtocol.JsonOptions);
                var framed = new byte[4 + payload.Length];
                BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
                payload.CopyTo(framed, 4);
                _response = new MemoryStream(framed);
            }
        }
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        switch (mode)
        {
            case Mode.FailRead:
                throw new IOException("pipe broken after the request was written");
            case Mode.HangRead:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            default:
                return _response is null ? 0 : _response.Read(buffer.Span);
        }
    }
}

public sealed class G01SessionReplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-g01-" + Guid.NewGuid().ToString("N"));
    private static readonly BuildIdentity Identity = new(CubaseConstants.PipeProtocolVersion, "r1", "sha", "server.exe");

    private static (BrokerClient Client, List<BrokerRequest> Received, List<ScriptedBrokerStream.Mode> Connections) Client(
        params ScriptedBrokerStream.Mode[] modes)
    {
        var received = new List<BrokerRequest>();
        var connections = new List<ScriptedBrokerStream.Mode>();
        var queue = new Queue<ScriptedBrokerStream.Mode>(modes);
        var client = new BrokerClient(_ =>
        {
            var mode = queue.Count > 0 ? queue.Dequeue() : ScriptedBrokerStream.Mode.Respond;
            connections.Add(mode);
            return Task.FromResult<Stream>(new ScriptedBrokerStream(mode, received));
        }, Identity);
        return (client, received, connections);
    }

    [Fact]
    public async Task SES01_WriteLostAfterSend_IsOutcomeUnknownAndNeverResent()
    {
        var (client, received, connections) = Client(ScriptedBrokerStream.Mode.FailRead, ScriptedBrokerStream.Mode.Respond);
        var request = new BrokerRequest("r1", "mixer", new JsonObject { ["action"] = "set", ["value"] = 0.5 }, OperationId: Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        Assert.Equal(ResultStatus.Unverified, response.Status);
        Assert.Equal(ExecutionState.Unknown, response.ExecutionState);
        Assert.Equal(ErrorCodes.OutcomeUnknown, response.Error!.Code);
        Assert.False(response.Error.Retryable);
        Assert.Single(received); // the write reached the broker exactly once
        Assert.Single(connections); // and no second connection was opened to resend it
        Assert.Equal(request.OperationId, response.OperationId);
    }

    [Fact]
    public async Task SES02_ConnectFailure_IsNotStartedWithZeroDispatch()
    {
        var attempts = 0;
        var client = new BrokerClient(_ => { attempts++; throw new IOException("broker unavailable"); }, Identity);
        var response = await client.SendAsync(new BrokerRequest("r1", "transport", new JsonObject { ["action"] = "play" }));
        Assert.Equal(ExecutionState.NotStarted, response.ExecutionState);
        Assert.Equal(ResultStatus.Offline, response.Status);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task SES03_StatelessDiagnosticRead_RetriesOnceOnNewConnectionThenStops()
    {
        var (client, received, connections) = Client(ScriptedBrokerStream.Mode.FailRead, ScriptedBrokerStream.Mode.Respond);
        var ok = await client.SendAsync(new BrokerRequest("s1", "status"));
        Assert.Equal(ResultStatus.Ok, ok.Status);
        Assert.Equal(2, received.Count);
        Assert.Equal(2, connections.Count);

        var (failing, failingReceived, failingConnections) = Client(ScriptedBrokerStream.Mode.FailRead, ScriptedBrokerStream.Mode.FailRead,
            ScriptedBrokerStream.Mode.Respond);
        var failed = await failing.SendAsync(new BrokerRequest("s2", "status"));
        Assert.Equal(ResultStatus.Error, failed.Status);
        Assert.Equal(2, failingReceived.Count); // one original + at most one retry
        Assert.Equal(2, failingConnections.Count);
    }

    [Fact]
    public async Task SES04_SessionScopedRead_IsNotReplayedAndReportsSessionLost()
    {
        var (client, received, connections) = Client(ScriptedBrokerStream.Mode.FailRead, ScriptedBrokerStream.Mode.Respond);
        var response = await client.SendAsync(new BrokerRequest("t1", "track", new JsonObject { ["action"] = "list" }));
        Assert.Equal(ErrorCodes.ClientSessionLost, response.Error!.Code);
        Assert.Equal(ExecutionState.NotStarted, response.ExecutionState);
        Assert.Single(received);
        Assert.Single(connections);

        // A confirmation issued to one broker session cannot be used by another session.
        var store = new ConfirmationStore();
        var args = new JsonObject { ["action"] = "delete" };
        var proposal = store.Prepare("session-old", "track.delete", args, "delete");
        Assert.False(store.Validate(proposal.ConfirmationId, "session-new", "track.delete", args).Ok);
    }

    [Fact]
    public async Task VER07_CancelledWhileWaiting_IsUnknownAndNextRequestStaysAligned()
    {
        var (client, received, _) = Client(ScriptedBrokerStream.Mode.HangRead, ScriptedBrokerStream.Mode.Respond);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = await client.SendAsync(new BrokerRequest("w1", "transport", new JsonObject { ["action"] = "play" }), cancel.Token);
        Assert.Equal(ExecutionState.Unknown, cancelled.ExecutionState);
        Assert.NotEqual(ExecutionState.NotStarted, cancelled.ExecutionState);
        var next = await client.SendAsync(new BrokerRequest("w2", "status"));
        Assert.Equal("w2", next.Id);
        Assert.Equal(ResultStatus.Ok, next.Status);

        var (mismatched, _, _) = Client(ScriptedBrokerStream.Mode.WrongId);
        var wrong = await mismatched.SendAsync(new BrokerRequest("w3", "transport", new JsonObject { ["action"] = "stop" }));
        Assert.Equal(ErrorCodes.OutcomeUnknown, wrong.Error!.Code);
        Assert.Equal(2, received.Count); // the cancelled write and the next read, each sent exactly once
    }

    private static MidiFrame Frame(MidiFrameKind kind, uint session, uint request, ushort opcode) =>
        new(kind, session, request, 1, opcode, 0, 1, []);

    private static PendingDriverRequest Pending(uint id, uint session, long epoch, string operation, string? nonce = null) =>
        new(id, session, epoch, MidiOperations.GetOpcode(operation), operation, nonce, "hash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(4));

    private static DriverFrameFilter AdoptedFilter(uint session)
    {
        var filter = new DriverFrameFilter();
        filter.Register(Pending(1, 0, 0, "system.hello", "nonce"));
        var hello = filter.Evaluate(Frame(MidiFrameKind.Response, session, 1, 1),
            new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["session_id"] = session, ["client_nonce"] = "nonce" } });
        Assert.Equal(FrameDisposition.AdoptHello, hello.Disposition);
        filter.AdoptSession(session);
        filter.Remove(1);
        return filter;
    }

    [Fact]
    public void SES05_EventFromOldSession_IsDiscardedWithoutTouchingState()
    {
        var filter = AdoptedFilter(77);
        var stale = filter.Evaluate(Frame(MidiFrameKind.Event, 76, 0, 254), new JsonObject { ["data"] = new JsonObject { ["key"] = "playing", ["value"] = true } });
        Assert.Equal(FrameDisposition.Discard, stale.Disposition);
        Assert.Equal(1, filter.StaleFramesDiscarded);
        Assert.Equal(FrameDisposition.AcceptEvent, filter.Evaluate(Frame(MidiFrameKind.Event, 77, 0, 254), new JsonObject()).Disposition);
    }

    [Fact]
    public void SES06_ResponseMustMatchIdOpcodeAndSession()
    {
        var filter = AdoptedFilter(77);
        filter.Register(Pending(10, 77, filter.Epoch, "mixer.set"));
        Assert.Equal(FrameDisposition.Discard, filter.Evaluate(Frame(MidiFrameKind.Response, 77, 11, 51), new JsonObject()).Disposition);
        Assert.Equal(FrameDisposition.Discard, filter.Evaluate(Frame(MidiFrameKind.Response, 77, 10, 50), new JsonObject()).Disposition);
        Assert.Equal(FrameDisposition.Discard, filter.Evaluate(Frame(MidiFrameKind.Response, 12, 10, 51), new JsonObject()).Disposition);
        var exact = filter.Evaluate(Frame(MidiFrameKind.Response, 77, 10, 51), new JsonObject { ["ok"] = true });
        Assert.Equal(FrameDisposition.CompletePending, exact.Disposition);
        Assert.Equal(10u, exact.Pending!.RequestId);
    }

    [Fact]
    public void SES07_HelloNeedsMatchingNonceAndHeaderSession()
    {
        var filter = new DriverFrameFilter();
        filter.Register(Pending(1, 0, 0, "system.hello", "right"));
        var wrongNonce = filter.Evaluate(Frame(MidiFrameKind.Response, 5, 1, 1),
            new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["session_id"] = 5, ["client_nonce"] = "wrong" } });
        var wrongSession = filter.Evaluate(Frame(MidiFrameKind.Response, 6, 1, 1),
            new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["session_id"] = 5, ["client_nonce"] = "right" } });
        var unsolicited = filter.Evaluate(Frame(MidiFrameKind.Response, 5, 9, 1),
            new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["session_id"] = 5, ["client_nonce"] = "right" } });
        Assert.Equal(FrameDisposition.Discard, wrongNonce.Disposition);
        Assert.Equal(FrameDisposition.Discard, wrongSession.Disposition);
        Assert.Equal(FrameDisposition.Discard, unsolicited.Disposition);
        Assert.Equal(0u, filter.CurrentSessionId);
        var epochBefore = filter.Epoch;
        var good = filter.Evaluate(Frame(MidiFrameKind.Response, 5, 1, 1),
            new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["session_id"] = 5, ["client_nonce"] = "right" } });
        Assert.Equal(FrameDisposition.AdoptHello, good.Disposition);
        filter.AdoptSession(5);
        Assert.Equal(epochBefore + 1, filter.Epoch);
    }

    [Fact]
    public void SES07_PendingFromOldEpoch_CannotBeCompletedAfterReconnect()
    {
        var filter = AdoptedFilter(77);
        filter.Register(Pending(20, 77, filter.Epoch, "transport.set"));
        var orphaned = filter.AdoptSession(78);
        Assert.Contains(orphaned, item => item.RequestId == 20);
        Assert.Equal(FrameDisposition.Discard, filter.Evaluate(Frame(MidiFrameKind.Response, 77, 20, 31), new JsonObject()).Disposition);
    }

    [Fact]
    public async Task SES09_Journal_ReusesCompletedResultRejectsConflictsAndRecoversDispatchingAsUnknown()
    {
        using var harness = new ControllerHarness();
        var channel = harness.Driver.AddChannel("Bass");
        var handle = await harness.HandleForAsync(channel);
        var id = Guid.NewGuid().ToString();
        JsonObject Args() => new() { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.25 };
        var first = await harness.SendAsync("mixer", Args(), operationId: id);
        Assert.Equal(ResultStatus.Ok, first.Status);
        Assert.Equal(1, harness.Driver.WriteCount("mixer.set"));

        var again = await harness.SendAsync("mixer", Args(), operationId: id);
        Assert.Equal(ResultStatus.Ok, again.Status);
        Assert.True(again.Data!["replayed_from_journal"]!.GetValue<bool>());
        Assert.Equal(1, harness.Driver.WriteCount("mixer.set"));

        var changed = Args();
        changed["value"] = 0.75;
        var conflict = await harness.SendAsync("mixer", changed, operationId: id);
        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.Error!.Code);
        Assert.Equal(1, harness.Driver.WriteCount("mixer.set"));

        // Crash while dispatching: a new journal instance over the same directory reports it as unknown.
        var journalRoot = Path.Combine(_root, "journal");
        var journal = new ExecutionJournal(journalRoot);
        var crashId = Guid.NewGuid().ToString();
        var begin = journal.Begin("s", crashId, "plan-hash", "mixer.set", null, false);
        journal.MarkDispatching(begin.Entry);
        var recovered = new ExecutionJournal(journalRoot);
        var entry = recovered.Get("s", crashId)!;
        Assert.Equal(JournalState.Unknown, entry.State);
        Assert.Equal(JournalBeginKind.OutcomeUnknown, recovered.Begin("s", crashId, "plan-hash", "mixer.set", null, false).Kind);
        Assert.Null(recovered.Get("other-session", crashId)); // another owner never sees it
    }

    [Fact]
    public void SES09_JournalRedactsLocalPaths()
    {
        var redacted = ExecutionJournal.Redact(new JsonObject { ["path"] = @"C:\Users\someone\Music\song.cpr", ["name"] = "Bass" })!;
        Assert.DoesNotContain("someone", redacted.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("song.cpr", redacted.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("Bass", redacted["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task SES01_DriverReplyLostAfterApply_IsUnknownAndWrittenOnce()
    {
        using var harness = new ControllerHarness();
        var channel = harness.Driver.AddChannel("Keys");
        var handle = await harness.HandleForAsync(channel);
        harness.Driver.LoseReplyAfterApply = true;
        var response = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.3 });
        Assert.Equal(ResultStatus.Unverified, response.Status);
        Assert.Equal(ExecutionState.Unknown, response.ExecutionState);
        Assert.Equal(ErrorCodes.OutcomeUnknown, response.Error!.Code);
        Assert.Equal(1, harness.Driver.WriteCount("mixer.set"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
