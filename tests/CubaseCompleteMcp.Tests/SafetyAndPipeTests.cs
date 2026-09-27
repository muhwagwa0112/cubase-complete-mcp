using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class SafetyAndPipeTests
{
    [Fact]
    public void Confirmation_IsSingleUseAndBoundToArguments()
    {
        var store = new ConfirmationStore(TimeSpan.FromMinutes(1));
        var arguments = new JsonObject { ["action"] = "delete", ["track_id"] = "track-1" };
        var proposal = store.Prepare("session-a", "track", arguments, "delete track");
        Assert.False(store.Consume(proposal.ConfirmationId, "session-a", "track", new JsonObject { ["action"] = "delete", ["track_id"] = "track-2" }));
        // A mismatched attempt must not burn the approval: the exact approved request still commits, once.
        Assert.True(store.Consume(proposal.ConfirmationId, "session-a", "track", arguments));
        Assert.False(store.Consume(proposal.ConfirmationId, "session-a", "track", arguments));
        var second = store.Prepare("session-a", "track", arguments, "delete track");
        Assert.False(store.Consume(second.ConfirmationId, "session-b", "track", arguments));
        var third = store.Prepare("session-a", "track", arguments, "delete track");
        Assert.True(store.Consume(third.ConfirmationId, "session-a", "track", arguments));
        Assert.False(store.Consume(third.ConfirmationId, "session-a", "track", arguments));
    }

    [Theory]
    [InlineData("track.delete")]
    [InlineData("project.overwrite")]
    [InlineData("project.record_start")]
    [InlineData("plugin.bulk_replace")]
    public void SafetyPolicy_ClassifiesDestructiveOperations(string operation) =>
        Assert.Equal(RiskLevel.Confirm, SafetyPolicy.Classify(operation, null));

    [Fact]
    public async Task PipeProtocol_RoundTripsWithSnakeCase()
    {
        await using var stream = new MemoryStream();
        var response = BrokerResponse.Success("x", new JsonObject { ["ok"] = true }, "test", sessionRevision: 7);
        await PipeProtocol.WriteAsync(stream, response);
        var bytes = stream.ToArray();
        var json = System.Text.Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4);
        Assert.Contains("\"path_used\"", json, StringComparison.Ordinal);
        Assert.Contains("\"session_revision\"", json, StringComparison.Ordinal);
        stream.Position = 0;
        var result = await PipeProtocol.ReadAsync<BrokerResponse>(stream);
        Assert.Equal(7, result!.Metadata!.SessionRevision);
    }

    [Fact]
    public async Task PipeProtocol_RejectsOversizedLength()
    {
        await using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, PipeProtocol.MaximumMessageBytes + 1);
        await stream.WriteAsync(header);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadAsync<JsonObject>(stream));
    }

    [Fact]
    public async Task PipeProtocol_ReturnsNullAtCleanEof()
    {
        await using var stream = new MemoryStream();
        Assert.Null(await PipeProtocol.ReadAsync<JsonObject>(stream));
    }

    [Fact]
    public void ArgumentHash_IsStableAndSensitive()
    {
        var a = SafetyPolicy.HashArguments("track", new JsonObject { ["id"] = 1 });
        Assert.Equal(a, SafetyPolicy.HashArguments("track", new JsonObject { ["id"] = 1 }));
        Assert.NotEqual(a, SafetyPolicy.HashArguments("track", new JsonObject { ["id"] = 2 }));
    }

    [Fact]
    public void CompleteCapabilityRegistry_IsReadOnly() =>
        Assert.Equal(RiskLevel.None, SafetyPolicy.Classify("capabilities.list", null));
}
