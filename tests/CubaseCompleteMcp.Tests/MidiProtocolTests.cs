using System.Buffers.Binary;
using System.Text;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class MidiProtocolTests
{
    [Fact]
    public void Pack7_RoundTripsAllByteValues()
    {
        var source = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var packed = MidiProtocolV2.Pack7(source);
        Assert.All(packed, value => Assert.InRange(value, (byte)0, (byte)0x7f));
        Assert.Equal(source, MidiProtocolV2.Unpack7(packed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(4097)]
    public void Pack7_RoundTripsLengths(int length)
    {
        var source = new byte[length];
        new Random(42).NextBytes(source);
        Assert.Equal(source, MidiProtocolV2.Unpack7(MidiProtocolV2.Pack7(source)));
    }

    [Fact]
    public void Crc32_MatchesStandardVector() =>
        Assert.Equal(0xcbf43926u, MidiProtocolV2.ComputeCrc32(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void Frame_RoundTripsEveryHeaderField()
    {
        var expected = new MidiFrame(MidiFrameKind.Response, 12, 34, 56, 78, 0, 1, [0, 127, 128, 255]);
        var actual = MidiProtocolV2.DecodeChunk(MidiProtocolV2.EncodeChunk(expected));
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Opcode, actual.Opcode);
        Assert.Equal(expected.Payload, actual.Payload);
    }

    [Fact]
    public void Frame_RejectsCrcMismatch()
    {
        var wire = MidiProtocolV2.EncodeChunk(new MidiFrame(MidiFrameKind.Request, 1, 2, 3, 4, 0, 1, [10, 20]));
        var raw = MidiProtocolV2.Unpack7(wire.AsSpan(4, wire.Length - 5));
        raw[^5] ^= 1;
        var repacked = MidiProtocolV2.Pack7(raw);
        var corrupt = new byte[repacked.Length + 5];
        wire.AsSpan(0, 4).CopyTo(corrupt);
        repacked.CopyTo(corrupt, 4);
        corrupt[^1] = 0xf7;
        Assert.Throws<InvalidDataException>(() => MidiProtocolV2.DecodeChunk(corrupt));
    }

    [Fact]
    public void Frame_RejectsMalformedPrefix() =>
        Assert.Throws<InvalidDataException>(() => MidiProtocolV2.DecodeChunk([0xf0, 0x01, 0xf7]));

    [Fact]
    public void Frame_RejectsEightBitPackedData()
    {
        var wire = MidiProtocolV2.EncodeChunk(new MidiFrame(MidiFrameKind.Ack, 1, 1, 1, 1, 0, 1, []));
        wire[5] = 0x80;
        Assert.Throws<InvalidDataException>(() => MidiProtocolV2.DecodeChunk(wire));
    }

    [Fact]
    public void Encode_ChunksAndReassemblesOutOfOrder()
    {
        var payload = Enumerable.Range(0, 1000).Select(index => (byte)(index % 251)).ToArray();
        var chunks = MidiProtocolV2.Encode(MidiFrameKind.Event, 10, 20, 30, 40, payload, 97)
            .Select(message => MidiProtocolV2.DecodeChunk(message)).Reverse().ToArray();
        var reassembler = new MidiFrameReassembler();
        MidiFrame? complete = null;
        foreach (var chunk in chunks) complete = reassembler.Add(chunk) ?? complete;
        Assert.NotNull(complete);
        Assert.Equal(payload, complete.Payload);
        Assert.Equal((ushort)1, complete.ChunkCount);
    }

    [Fact]
    public void Reassembler_AcceptsIdenticalDuplicate()
    {
        var chunks = MidiProtocolV2.Encode(MidiFrameKind.Response, 1, 2, 3, 4, new byte[20], 10)
            .Select(message => MidiProtocolV2.DecodeChunk(message)).ToArray();
        var reassembler = new MidiFrameReassembler();
        Assert.Null(reassembler.Add(chunks[0]));
        Assert.Null(reassembler.Add(chunks[0]));
        Assert.NotNull(reassembler.Add(chunks[1]));
    }

    [Fact]
    public void Reassembler_RejectsConflictingDuplicate()
    {
        var first = new MidiFrame(MidiFrameKind.Response, 1, 2, 3, 4, 0, 2, [1]);
        var conflicting = first with { Payload = [2] };
        var reassembler = new MidiFrameReassembler();
        Assert.Null(reassembler.Add(first));
        Assert.Throws<InvalidDataException>(() => reassembler.Add(conflicting));
    }

    [Fact]
    public void Encode_RejectsOversizedLogicalPayload() =>
        Assert.Throws<InvalidDataException>(() => MidiProtocolV2.Encode(MidiFrameKind.Request, 0, 0, 0, 1,
            new byte[MidiProtocolV2.MaximumLogicalPayloadBytes + 1]));

    [Fact]
    public void OpcodeMap_IsUniqueAndRoundTrips()
    {
        Assert.Equal(MidiOperations.All.Count, MidiOperations.All.Values.Distinct().Count());
        foreach (var pair in MidiOperations.All) Assert.Equal(pair.Key, MidiOperations.GetName(pair.Value));
    }
}
