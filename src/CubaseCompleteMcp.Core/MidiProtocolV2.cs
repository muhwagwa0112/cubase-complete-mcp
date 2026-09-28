using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace CubaseCompleteMcp.Core;

public enum MidiFrameKind : byte
{
    Request = 1,
    Response = 2,
    Event = 3,
    Ack = 4,
    Nack = 5,
    Heartbeat = 6
}

public sealed record MidiFrame(
    MidiFrameKind Kind,
    uint SessionId,
    uint RequestId,
    uint Sequence,
    ushort Opcode,
    ushort ChunkIndex,
    ushort ChunkCount,
    byte[] Payload);

public static class MidiOperations
{
    private static readonly Dictionary<string, ushort> NameToOpcode = new(StringComparer.Ordinal)
    {
        ["system.hello"] = 1,
        ["system.heartbeat"] = 2,
        ["status.get"] = 10,
        ["capabilities.get"] = 11,
        ["command.execute"] = 20,
        ["command.can_perform"] = 21,
        ["transport.get"] = 30,
        ["transport.set"] = 31,
        ["track.get"] = 40,
        ["track.set"] = 41,
        ["track.list"] = 42,
        ["mixer.get"] = 50,
        ["mixer.set"] = 51,
        ["plugin.get"] = 60,
        ["plugin.set"] = 61,
        ["project.get"] = 70,
        ["project.set"] = 71,
        ["direct.get"] = 80,
        ["direct.set"] = 81,
        ["state.event"] = 254,
        ["error"] = 255
    };

    private static readonly Dictionary<ushort, string> OpcodeToName =
        NameToOpcode.ToDictionary(static pair => pair.Value, static pair => pair.Key);

    public static ushort GetOpcode(string operation) => NameToOpcode.TryGetValue(operation, out var opcode)
        ? opcode
        : throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown MIDI operation.");

    public static string GetName(ushort opcode) => OpcodeToName.TryGetValue(opcode, out var name)
        ? name
        : throw new InvalidDataException($"Unknown MIDI opcode: {opcode}.");

    public static IReadOnlyDictionary<string, ushort> All => NameToOpcode;

    /// <summary>Driver operations that can change Cubase. A lost reply to one of these is an unknown outcome.</summary>
    public static bool IsWrite(string operation) =>
        operation.EndsWith(".set", StringComparison.Ordinal) || operation == "command.execute";
}

public static class MidiProtocolV2
{
    public const byte Version = 2;
    public const int HeaderBytes = 28;
    public const int CrcBytes = 4;
    public const int DefaultChunkPayloadBytes = 2048;
    public const int MaximumLogicalPayloadBytes = 8 * 1024 * 1024;
    public const int MaximumWireBytes = 64 * 1024;

    private static readonly byte[] SysExPrefix = [0xF0, 0x7D, 0x43, 0x42];
    private static readonly byte[] Magic = [0x43, 0x42, 0x32];

    public static IReadOnlyList<byte[]> Encode(
        MidiFrameKind kind,
        uint sessionId,
        uint requestId,
        uint firstSequence,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        int chunkPayloadBytes = DefaultChunkPayloadBytes)
    {
        if (payload.Length > MaximumLogicalPayloadBytes)
        {
            throw new InvalidDataException("Logical MIDI payload exceeds the configured limit.");
        }

        if (chunkPayloadBytes <= 0 || chunkPayloadBytes > 32 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkPayloadBytes));
        }

        var count = Math.Max(1, (payload.Length + chunkPayloadBytes - 1) / chunkPayloadBytes);
        if (count > ushort.MaxValue)
        {
            throw new InvalidDataException("MIDI payload requires too many chunks.");
        }

        var messages = new List<byte[]>(count);
        for (var index = 0; index < count; index++)
        {
            var start = index * chunkPayloadBytes;
            var length = Math.Min(chunkPayloadBytes, payload.Length - start);
            var chunk = length == 0 ? ReadOnlySpan<byte>.Empty : payload.Slice(start, length);
            messages.Add(EncodeChunk(new MidiFrame(kind, sessionId, requestId,
                firstSequence + (uint)index, opcode, (ushort)index, (ushort)count, chunk.ToArray())));
        }

        return messages;
    }

    public static byte[] EncodeChunk(MidiFrame frame)
    {
        if (frame.ChunkCount == 0 || frame.ChunkIndex >= frame.ChunkCount)
        {
            throw new InvalidDataException("Invalid MIDI chunk coordinates.");
        }

        var raw = new byte[HeaderBytes + frame.Payload.Length + CrcBytes];
        Magic.CopyTo(raw, 0);
        raw[3] = Version;
        raw[4] = (byte)frame.Kind;
        raw[5] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(6, 4), frame.SessionId);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(10, 4), frame.RequestId);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(14, 4), frame.Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(18, 2), frame.Opcode);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(20, 2), frame.ChunkIndex);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(22, 2), frame.ChunkCount);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(24, 4), (uint)frame.Payload.Length);
        frame.Payload.CopyTo(raw, HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(raw.Length - CrcBytes), ComputeCrc32(raw.AsSpan(0, raw.Length - CrcBytes)));

        var packed = Pack7(raw);
        var wire = new byte[SysExPrefix.Length + packed.Length + 1];
        SysExPrefix.CopyTo(wire, 0);
        packed.CopyTo(wire, SysExPrefix.Length);
        wire[^1] = 0xF7;
        if (wire.Length > MaximumWireBytes)
        {
            throw new InvalidDataException("Encoded SysEx frame exceeds the configured wire limit.");
        }

        return wire;
    }

    public static MidiFrame DecodeChunk(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < SysExPrefix.Length + 2 || !wire[..SysExPrefix.Length].SequenceEqual(SysExPrefix) || wire[^1] != 0xF7)
        {
            throw new InvalidDataException("Not a Cubase Complete MCP SysEx v2 frame.");
        }

        if (wire.Length > MaximumWireBytes)
        {
            throw new InvalidDataException("SysEx frame exceeds the configured wire limit.");
        }

        var raw = Unpack7(wire.Slice(SysExPrefix.Length, wire.Length - SysExPrefix.Length - 1));
        if (raw.Length < HeaderBytes + CrcBytes || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("MIDI frame has invalid magic or is truncated.");
        }

        if (raw[3] != Version || raw[5] != 0)
        {
            throw new InvalidDataException("MIDI frame version or flags are unsupported.");
        }

        if (!Enum.IsDefined(typeof(MidiFrameKind), raw[4]))
        {
            throw new InvalidDataException("MIDI frame kind is invalid.");
        }

        var payloadLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(24, 4)));
        var expectedLength = HeaderBytes + payloadLength + CrcBytes;
        if (raw.Length != expectedLength)
        {
            throw new InvalidDataException("MIDI frame payload length does not match the frame length.");
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(raw.Length - CrcBytes));
        var actualCrc = ComputeCrc32(raw.AsSpan(0, raw.Length - CrcBytes));
        if (expectedCrc != actualCrc)
        {
            throw new InvalidDataException("MIDI frame CRC32 mismatch.");
        }

        var chunkIndex = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(20, 2));
        var chunkCount = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(22, 2));
        if (chunkCount == 0 || chunkIndex >= chunkCount)
        {
            throw new InvalidDataException("MIDI frame chunk coordinates are invalid.");
        }

        return new MidiFrame(
            (MidiFrameKind)raw[4],
            BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(6, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(10, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(14, 4)),
            BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(18, 2)),
            chunkIndex,
            chunkCount,
            raw.AsSpan(HeaderBytes, payloadLength).ToArray());
    }

    public static byte[] Pack7(ReadOnlySpan<byte> source)
    {
        var output = new List<byte>(source.Length + (source.Length + 6) / 7);
        for (var offset = 0; offset < source.Length; offset += 7)
        {
            var count = Math.Min(7, source.Length - offset);
            byte mask = 0;
            for (var index = 0; index < count; index++)
            {
                if ((source[offset + index] & 0x80) != 0)
                {
                    mask |= (byte)(1 << index);
                }
            }

            output.Add(mask);
            for (var index = 0; index < count; index++)
            {
                output.Add((byte)(source[offset + index] & 0x7F));
            }
        }

        return output.ToArray();
    }

    public static byte[] Unpack7(ReadOnlySpan<byte> source)
    {
        var output = new List<byte>(source.Length);
        var offset = 0;
        while (offset < source.Length)
        {
            var mask = source[offset++];
            if (mask > 0x7F || offset >= source.Length)
            {
                throw new InvalidDataException("Invalid 7-bit packed group.");
            }

            var count = Math.Min(7, source.Length - offset);
            if ((mask >> count) != 0)
            {
                throw new InvalidDataException("7-bit packed mask references missing data bytes.");
            }

            for (var index = 0; index < count; index++)
            {
                var value = source[offset++];
                if (value > 0x7F)
                {
                    throw new InvalidDataException("Packed MIDI data contains an 8-bit wire byte.");
                }

                output.Add((byte)(value | (((mask >> index) & 1) << 7)));
            }
        }

        return output.ToArray();
    }

    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}

public sealed class MidiFrameReassembler(TimeSpan? timeout = null)
{
    private readonly ConcurrentDictionary<FrameKey, PendingFrame> _pending = new();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public MidiFrame? Add(MidiFrame chunk)
    {
        Prune();
        if (chunk.ChunkCount == 1)
        {
            return chunk;
        }

        var key = new FrameKey(chunk.Kind, chunk.SessionId, chunk.RequestId, chunk.Opcode);
        var pending = _pending.GetOrAdd(key, _ => new PendingFrame(chunk.ChunkCount));
        lock (pending)
        {
            if (pending.ChunkCount != chunk.ChunkCount)
            {
                _pending.TryRemove(key, out _);
                throw new InvalidDataException("MIDI chunk count changed during reassembly.");
            }

            pending.Add(chunk);
            if (!pending.IsComplete)
            {
                return null;
            }

            _pending.TryRemove(key, out _);
            var payload = pending.Join();
            if (payload.Length > MidiProtocolV2.MaximumLogicalPayloadBytes)
            {
                throw new InvalidDataException("Reassembled MIDI payload exceeds the configured limit.");
            }

            return chunk with { ChunkIndex = 0, ChunkCount = 1, Payload = payload };
        }
    }

    private void Prune()
    {
        var threshold = DateTimeOffset.UtcNow - _timeout;
        foreach (var item in _pending)
        {
            if (item.Value.CreatedAt < threshold)
            {
                _pending.TryRemove(item.Key, out _);
            }
        }
    }

    private readonly record struct FrameKey(MidiFrameKind Kind, uint Session, uint Request, ushort Opcode);

    private sealed class PendingFrame(ushort chunkCount)
    {
        private readonly MidiFrame?[] _chunks = new MidiFrame?[chunkCount];
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public ushort ChunkCount { get; } = chunkCount;
        public bool IsComplete => _chunks.All(static value => value is not null);

        public void Add(MidiFrame frame)
        {
            var existing = _chunks[frame.ChunkIndex];
            if (existing is not null && !existing.Payload.AsSpan().SequenceEqual(frame.Payload))
            {
                throw new InvalidDataException("Duplicate MIDI chunk has different content.");
            }

            _chunks[frame.ChunkIndex] = frame;
        }

        public byte[] Join()
        {
            var length = _chunks.Sum(static item => item?.Payload.Length ?? 0);
            var payload = new byte[length];
            var offset = 0;
            foreach (var item in _chunks)
            {
                item!.Payload.CopyTo(payload, offset);
                offset += item.Payload.Length;
            }

            return payload;
        }
    }
}

