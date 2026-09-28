using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

/// <summary>Captures serialized broker requests written by the real MCP tool methods and answers with Ok.</summary>
internal sealed class CapturingPipe(List<BrokerRequest> received) : Stream
{
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
    public override int Read(byte[] buffer, int offset, int count) => _response?.Read(buffer, offset, count) ?? 0;
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _inbound.Write(buffer.Span);
        var bytes = _inbound.ToArray();
        if (bytes.Length < 4) return ValueTask.CompletedTask;
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes);
        if (bytes.Length < 4 + length) return ValueTask.CompletedTask;
        var request = JsonSerializer.Deserialize<BrokerRequest>(bytes.AsSpan(4, length), PipeProtocol.JsonOptions)!;
        received.Add(request);
        _inbound.SetLength(0);
        var payload = JsonSerializer.SerializeToUtf8Bytes(BrokerResponse.Success(request.Id, new JsonObject(), "capture"), PipeProtocol.JsonOptions);
        var framed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
        payload.CopyTo(framed, 4);
        _response = new MemoryStream(framed);
        return ValueTask.CompletedTask;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_response?.Read(buffer.Span) ?? 0);
}

public sealed class McpContractTests
{
    private static (CubaseTools Tools, List<BrokerRequest> Received) Tools()
    {
        var received = new List<BrokerRequest>();
        var client = new BrokerClient(_ => Task.FromResult<Stream>(new CapturingPipe(received)),
            new BuildIdentity(CubaseConstants.PipeProtocolVersion, null, null, "server.exe"));
        return (new CubaseTools(client), received);
    }

    private static JsonElement Properties(string method) =>
        McpServerTool.Create(typeof(CubaseTools).GetMethod(method)!, typeof(CubaseTools)).ProtocolTool.InputSchema.GetProperty("properties");

    [Fact]
    public void BAT01_BatchSchemaExposesEveryOption()
    {
        var properties = Properties(nameof(CubaseTools.Batch));
        foreach (var name in new[] { "operations", "operation_id", "confirmation_id", "continue_on_unverified", "timeout_ms", "resume_operation_id" })
            Assert.True(properties.TryGetProperty(name, out _), $"cubase_batch schema lacks {name}");
    }

    [Fact]
    public async Task BAT01_BatchOptionsReachTheBrokerRequest()
    {
        var (tools, received) = Tools();
        using var operations = JsonDocument.Parse("""[{"operation":"transport","arguments":{"action":"play"}}]""");
        await tools.Batch(operations.RootElement.Clone(), continue_on_unverified: true, timeout_ms: 5000);
        var request = Assert.Single(received);
        Assert.Equal("batch", request.Operation);
        Assert.True(request.Arguments!["continue_on_unverified"]!.GetValue<bool>());
        Assert.Equal(5000, request.Arguments["timeout_ms"]!.GetValue<int>());
        Assert.True(Guid.TryParse(request.OperationId, out _));
        Assert.Equal(CubaseConstants.PipeProtocolVersion, request.ClientProtocolVersion);
    }

    [Fact]
    public void API01_TargetTimeAndOperationArgumentsAreInTheSchemas()
    {
        foreach (var tool in new[] { nameof(CubaseTools.Mixer), nameof(CubaseTools.Track), nameof(CubaseTools.Plugin), nameof(CubaseTools.Direct), nameof(CubaseTools.Command) })
        {
            var properties = Properties(tool);
            foreach (var name in new[] { "target_handle", "target_scope", "bank_index", "operation_id" })
                Assert.True(properties.TryGetProperty(name, out _), $"{tool} lacks {name}");
        }
        var transport = Properties(nameof(CubaseTools.Transport));
        Assert.True(transport.TryGetProperty("position_text", out _));
        Assert.True(transport.TryGetProperty("time_format", out _));
        Assert.False(transport.TryGetProperty("value", out _)); // the ambiguous protocol-1 field is gone
        var project = Properties(nameof(CubaseTools.Project));
        Assert.True(project.TryGetProperty("expected_duration_seconds", out _));
        Assert.True(Properties(nameof(CubaseTools.Diagnostics)).TryGetProperty("operation_id", out _));
    }

    [Fact]
    public async Task API01_LegacyTrackIdIsForwardedSoTheBrokerRejectsItInsteadOfIgnoringIt()
    {
        var (tools, received) = Tools();
        await tools.Mixer("set", parameter: "volume", process_value: 0.5, track_id: "3");
        Assert.Equal("3", Assert.Single(received).Arguments!["track_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task API01_ReadsGetNoOperationIdAndCommitsKeepTheStoredOne()
    {
        var (tools, received) = Tools();
        await tools.Track("list");
        await tools.Track("delete", target_scope: "selected", confirmation_id: "c1");
        await tools.Transport("play");
        Assert.Null(received[0].OperationId);
        Assert.Null(received[1].OperationId); // the broker restores the operation_id stored with the approved plan
        Assert.NotNull(received[2].OperationId);
    }

    [Fact]
    public void API02_ProtocolTwoIsDeclaredAndOlderClientsAreRefused()
    {
        Assert.Equal("CubaseCompleteMcp.v2", CubaseConstants.PipeName);
        Assert.Equal(2, CubaseConstants.PipeProtocolVersion);
        var broker = new BuildIdentity(2, "r", "s", "broker.exe");
        Assert.False(BrokerCompatibility.Evaluate(1, "r", broker).ProtocolCompatible);
        Assert.False(BrokerCompatibility.Evaluate(null, "r", broker).ProtocolCompatible);
        Assert.True(BrokerCompatibility.Evaluate(2, "r", broker).ProtocolCompatible);
    }
}
