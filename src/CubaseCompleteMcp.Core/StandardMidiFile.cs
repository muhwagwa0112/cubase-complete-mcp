using System.Buffers.Binary;
using System.Text;

namespace CubaseCompleteMcp.Core;

public static class StandardMidiFile
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Write(MidiCompositionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var output = new MemoryStream();
        output.Write("MThd"u8);
        WriteUInt32(output, 6);
        WriteUInt16(output, document.Tracks.Count > 1 ? 1 : 0);
        WriteUInt16(output, checked((ushort)document.Tracks.Count));
        WriteUInt16(output, checked((ushort)document.Ppq));
        foreach (var track in document.Tracks) WriteTrack(output, track);
        return output.ToArray();
    }

    public static MidiCompositionDocument Read(ReadOnlySpan<byte> bytes, string? name = null)
    {
        if (bytes.Length < 14 || !bytes[..4].SequenceEqual("MThd"u8)) throw new InvalidDataException("Missing MIDI MThd header.");
        var headerLength = ReadUInt32(bytes, 4);
        if (headerLength < 6 || 8L + headerLength > bytes.Length) throw new InvalidDataException("Invalid MIDI header length.");
        var format = ReadUInt16(bytes, 8);
        var trackCount = ReadUInt16(bytes, 10);
        var division = ReadUInt16(bytes, 12);
        if (format > 1) throw new InvalidDataException($"MIDI format {format} is not supported; format 0 and 1 are supported.");
        if (format == 0 && trackCount != 1) throw new InvalidDataException("MIDI format 0 must contain exactly one track.");
        if ((division & 0x8000) != 0) throw new InvalidDataException("SMPTE time division is not supported for editable composition documents.");
        if (division is < 24 or > 32767) throw new InvalidDataException("MIDI PPQ division is invalid.");
        if (trackCount is 0 or > MidiCompositionService.MaximumTracks) throw new InvalidDataException("MIDI track count is invalid.");

        var document = new MidiCompositionDocument
        {
            Id = "midi_imported", Name = string.IsNullOrWhiteSpace(name) ? "Imported MIDI" : name,
            Ppq = division, Tracks = []
        };
        var offset = checked((int)(8 + headerLength));
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            if (offset + 8 > bytes.Length || !bytes.Slice(offset, 4).SequenceEqual("MTrk"u8))
                throw new InvalidDataException($"Missing MTrk header for track {trackIndex}.");
            var length = ReadUInt32(bytes, offset + 4);
            offset += 8;
            if (length > int.MaxValue || offset + (long)length > bytes.Length) throw new InvalidDataException("MIDI track length exceeds the file.");
            document.Tracks.Add(ReadTrack(bytes.Slice(offset, checked((int)length)), trackIndex));
            offset += checked((int)length);
        }
        if (document.Tracks.Sum(track => track.Events.Count) > MidiCompositionService.MaximumEvents)
            throw new InvalidDataException($"MIDI file exceeds {MidiCompositionService.MaximumEvents} decoded events.");
        return document;
    }

    private static void WriteTrack(Stream output, MidiCompositionTrack track)
    {
        var events = new List<WireEvent>();
        events.Add(new WireEvent(0, 0, Meta(0x03, Utf8.GetBytes(track.Name))));
        foreach (var item in track.Events) AddWireEvents(events, item, item.Tick, track.Channel);
        foreach (var part in track.Parts)
            foreach (var item in part.Events)
                AddWireEvents(events, item, checked(part.StartTick + item.Tick), track.Channel);
        events.Sort(static (left, right) =>
        {
            var tick = left.Tick.CompareTo(right.Tick);
            return tick != 0 ? tick : left.Order.CompareTo(right.Order);
        });
        using var body = new MemoryStream();
        long previous = 0;
        foreach (var item in events)
        {
            WriteVariable(body, checked((uint)(item.Tick - previous)));
            body.Write(item.Data);
            previous = item.Tick;
        }
        WriteVariable(body, 0);
        body.Write([0xff, 0x2f, 0x00]);
        output.Write("MTrk"u8);
        WriteUInt32(output, checked((uint)body.Length));
        body.Position = 0;
        body.CopyTo(output);
    }

    private static void AddWireEvents(List<WireEvent> target, MidiCompositionEvent item, long tick, int trackChannel)
    {
        if (tick is < 0 or > MidiCompositionService.MaximumTick) throw new InvalidDataException("Rendered event tick is outside the supported range.");
        var channel = item.Channel ?? trackChannel;
        var statusChannel = checked((byte)channel);
        switch (item.Type)
        {
            case "note":
                target.Add(new WireEvent(tick, 20, [(byte)(0x90 | statusChannel), checked((byte)item.Note!.Value), checked((byte)item.Velocity!.Value)]));
                target.Add(new WireEvent(checked(tick + item.DurationTicks), 10,
                    [(byte)(0x80 | statusChannel), checked((byte)item.Note.Value), checked((byte)(item.ReleaseVelocity ?? 64))]));
                break;
            case "cc": target.Add(new WireEvent(tick, 30, [(byte)(0xb0 | statusChannel), checked((byte)item.Controller!.Value), checked((byte)item.Value!.Value)])); break;
            case "program_change": target.Add(new WireEvent(tick, 30, [(byte)(0xc0 | statusChannel), checked((byte)item.Program!.Value)])); break;
            case "pitch_bend":
                var bend = item.Bend!.Value + 8192;
                target.Add(new WireEvent(tick, 30, [(byte)(0xe0 | statusChannel), (byte)(bend & 0x7f), (byte)((bend >> 7) & 0x7f)]));
                break;
            case "channel_pressure": target.Add(new WireEvent(tick, 30, [(byte)(0xd0 | statusChannel), checked((byte)item.Pressure!.Value)])); break;
            case "poly_pressure": target.Add(new WireEvent(tick, 30, [(byte)(0xa0 | statusChannel), checked((byte)item.Note!.Value), checked((byte)item.Pressure!.Value)])); break;
            case "tempo":
                var microseconds = checked((int)Math.Round(60_000_000d / item.Bpm!.Value));
                target.Add(new WireEvent(tick, 0, [0xff, 0x51, 0x03, (byte)(microseconds >> 16), (byte)(microseconds >> 8), (byte)microseconds]));
                break;
            case "time_signature":
                var exponent = 0;
                for (var value = item.Denominator!.Value; value > 1; value >>= 1) exponent++;
                target.Add(new WireEvent(tick, 0, [0xff, 0x58, 0x04, checked((byte)item.Numerator!.Value), checked((byte)exponent), 24, 8]));
                break;
            case "key_signature":
                target.Add(new WireEvent(tick, 0, [0xff, 0x59, 0x02, unchecked((byte)(sbyte)item.KeySharpsFlats!.Value), item.Minor == true ? (byte)1 : (byte)0]));
                break;
            case "marker": target.Add(new WireEvent(tick, 0, Meta(0x06, Utf8.GetBytes(item.Text!)))); break;
            case "lyric": target.Add(new WireEvent(tick, 0, Meta(0x05, Utf8.GetBytes(item.Text!)))); break;
            case "text": target.Add(new WireEvent(tick, 0, Meta(0x01, Utf8.GetBytes(item.Text!)))); break;
            case "sysex":
                var sysex = MidiCompositionService.ParseHex(item.DataHex!);
                target.Add(new WireEvent(tick, 0, WithVariablePrefix(0xf0, sysex)));
                break;
            case "meta": target.Add(new WireEvent(tick, 0, Meta(checked((byte)item.MetaType!.Value), MidiCompositionService.ParseHex(item.DataHex!)))); break;
            default: throw new InvalidDataException($"Unsupported MIDI event type '{item.Type}'.");
        }
    }

    private static MidiCompositionTrack ReadTrack(ReadOnlySpan<byte> data, int index)
    {
        var track = new MidiCompositionTrack { Id = NewImportId("trk", index), Name = $"Track {index + 1}", Channel = 0 };
        var pending = new Dictionary<(int Channel, int Note), Queue<PendingNote>>();
        long tick = 0;
        var offset = 0;
        byte runningStatus = 0;
        var sequence = 0;
        while (offset < data.Length)
        {
            var delta = ReadVariable(data, ref offset);
            tick = checked(tick + delta);
            if (tick > MidiCompositionService.MaximumTick) throw new InvalidDataException("MIDI event tick exceeds the supported range.");
            if (offset >= data.Length) throw new InvalidDataException("MIDI event is truncated after delta time.");
            byte status;
            if ((data[offset] & 0x80) != 0)
            {
                status = data[offset++];
                if (status < 0xf0) runningStatus = status;
            }
            else
            {
                if (runningStatus == 0) throw new InvalidDataException("MIDI running status has no preceding channel status.");
                status = runningStatus;
            }

            if (status == 0xff)
            {
                runningStatus = 0;
                RequireBytes(data, offset, 1);
                var type = data[offset++];
                var length = checked((int)ReadVariable(data, ref offset));
                RequireBytes(data, offset, length);
                var payload = data.Slice(offset, length);
                offset += length;
                if (type == 0x2f) break;
                DecodeMeta(track, tick, type, payload, sequence++);
                continue;
            }
            if (status is 0xf0 or 0xf7)
            {
                runningStatus = 0;
                var length = checked((int)ReadVariable(data, ref offset));
                RequireBytes(data, offset, length);
                track.Events.Add(NewEvent("sysex", tick, sequence++, dataHex: Convert.ToHexString(data.Slice(offset, length)).ToLowerInvariant()));
                offset += length;
                continue;
            }
            if (status >= 0xf0) throw new InvalidDataException($"Unsupported system MIDI status 0x{status:x2} in track data.");

            var family = status & 0xf0;
            var channel = status & 0x0f;
            var lengthForMessage = family is 0xc0 or 0xd0 ? 1 : 2;
            RequireBytes(data, offset, lengthForMessage);
            var first = data[offset++];
            var second = lengthForMessage == 2 ? data[offset++] : (byte)0;
            if (first > 0x7f || second > 0x7f) throw new InvalidDataException("MIDI channel data contains an 8-bit value.");
            track.Channel = channel;
            switch (family)
            {
                case 0x80: FinishNote(track, pending, tick, channel, first, second, sequence++); break;
                case 0x90:
                    if (second == 0) FinishNote(track, pending, tick, channel, first, 0, sequence++);
                    else
                    {
                        var key = (channel, (int)first);
                        if (!pending.TryGetValue(key, out var queue)) pending[key] = queue = new Queue<PendingNote>();
                        queue.Enqueue(new PendingNote(tick, second, sequence++));
                    }
                    break;
                case 0xa0: track.Events.Add(NewEvent("poly_pressure", tick, sequence++, channel, note: first, pressure: second)); break;
                case 0xb0: track.Events.Add(NewEvent("cc", tick, sequence++, channel, controller: first, value: second)); break;
                case 0xc0: track.Events.Add(NewEvent("program_change", tick, sequence++, channel, program: first)); break;
                case 0xd0: track.Events.Add(NewEvent("channel_pressure", tick, sequence++, channel, pressure: first)); break;
                case 0xe0: track.Events.Add(NewEvent("pitch_bend", tick, sequence++, channel, bend: (first | second << 7) - 8192)); break;
            }
        }
        foreach (var pair in pending)
            while (pair.Value.TryDequeue(out var note))
                track.Events.Add(NewEvent("note", note.Tick, note.Sequence, pair.Key.Channel, pair.Key.Note,
                    note.Velocity, duration: Math.Max(1, tick - note.Tick), releaseVelocity: 0));
        track.Events.Sort(static (left, right) => left.Tick.CompareTo(right.Tick));
        return track;
    }

    private static void FinishNote(MidiCompositionTrack track, Dictionary<(int Channel, int Note), Queue<PendingNote>> pending,
        long tick, int channel, int note, int releaseVelocity, int sequence)
    {
        var key = (channel, note);
        if (!pending.TryGetValue(key, out var queue) || !queue.TryDequeue(out var start)) return;
        track.Events.Add(NewEvent("note", start.Tick, start.Sequence, channel, note, start.Velocity,
            Math.Max(1, tick - start.Tick), releaseVelocity));
        if (queue.Count == 0) pending.Remove(key);
        _ = sequence;
    }

    private static void DecodeMeta(MidiCompositionTrack track, long tick, int type, ReadOnlySpan<byte> payload, int sequence)
    {
        switch (type)
        {
            case 0x03: track.Name = DecodeText(payload); break;
            case 0x01: track.Events.Add(NewEvent("text", tick, sequence, text: DecodeText(payload))); break;
            case 0x05: track.Events.Add(NewEvent("lyric", tick, sequence, text: DecodeText(payload))); break;
            case 0x06: track.Events.Add(NewEvent("marker", tick, sequence, text: DecodeText(payload))); break;
            case 0x51 when payload.Length == 3:
                var micros = payload[0] << 16 | payload[1] << 8 | payload[2];
                if (micros > 0) track.Events.Add(NewEvent("tempo", tick, sequence, bpm: 60_000_000d / micros));
                break;
            case 0x58 when payload.Length >= 2 && payload[1] <= 7:
                track.Events.Add(NewEvent("time_signature", tick, sequence, numerator: payload[0], denominator: 1 << payload[1]));
                break;
            case 0x59 when payload.Length >= 2 && unchecked((sbyte)payload[0]) is >= -7 and <= 7 && payload[1] <= 1:
                track.Events.Add(NewEvent("key_signature", tick, sequence, keySharpsFlats: unchecked((sbyte)payload[0]), minor: payload[1] != 0));
                break;
            default:
                track.Events.Add(NewEvent("meta", tick, sequence, metaType: type, dataHex: Convert.ToHexString(payload).ToLowerInvariant()));
                break;
        }
    }

    private static MidiCompositionEvent NewEvent(string type, long tick, int sequence, int? channel = null,
        int? note = null, int? velocity = null, long duration = 0, int? releaseVelocity = null,
        int? controller = null, int? value = null, int? program = null, int? bend = null, int? pressure = null,
        double? bpm = null, int? numerator = null, int? denominator = null, int? keySharpsFlats = null,
        bool? minor = null, string? text = null, int? metaType = null, string? dataHex = null) => new()
    {
        Id = $"evt_import_{sequence:x8}", Type = type, Tick = tick, Channel = channel, Note = note,
        Velocity = velocity, DurationTicks = duration, ReleaseVelocity = releaseVelocity, Controller = controller,
        Value = value, Program = program, Bend = bend, Pressure = pressure, Bpm = bpm, Numerator = numerator,
        Denominator = denominator, KeySharpsFlats = keySharpsFlats, Minor = minor, Text = text,
        MetaType = metaType, DataHex = dataHex
    };

    private static byte[] Meta(byte type, byte[] payload)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0xff); stream.WriteByte(type); WriteVariable(stream, checked((uint)payload.Length)); stream.Write(payload);
        return stream.ToArray();
    }

    private static byte[] WithVariablePrefix(byte status, byte[] payload)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(status); WriteVariable(stream, checked((uint)payload.Length)); stream.Write(payload);
        return stream.ToArray();
    }

    private static string DecodeText(ReadOnlySpan<byte> payload)
    {
        try { return Utf8.GetString(payload); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(payload); }
    }

    private static void WriteVariable(Stream stream, uint value)
    {
        if (value > 0x0fffffff) throw new InvalidDataException("MIDI variable-length value exceeds 28 bits.");
        Span<byte> buffer = stackalloc byte[4];
        var index = 3;
        buffer[index] = (byte)(value & 0x7f);
        while ((value >>= 7) != 0) buffer[--index] = (byte)((value & 0x7f) | 0x80);
        stream.Write(buffer[index..]);
    }

    private static uint ReadVariable(ReadOnlySpan<byte> data, ref int offset)
    {
        uint value = 0;
        for (var count = 0; count < 4; count++)
        {
            RequireBytes(data, offset, 1);
            var item = data[offset++];
            value = checked((value << 7) | (uint)(item & 0x7f));
            if ((item & 0x80) == 0) return value;
        }
        throw new InvalidDataException("MIDI variable-length value exceeds four bytes.");
    }

    private static void RequireBytes(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + (long)count > data.Length) throw new InvalidDataException("MIDI event payload is truncated.");
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(buffer, checked((ushort)value)); stream.Write(buffer);
    }
    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(buffer, value); stream.Write(buffer);
    }
    private static string NewImportId(string prefix, int index) => $"{prefix}_import_{index:x4}";
    private sealed record WireEvent(long Tick, int Order, byte[] Data);
    private sealed record PendingNote(long Tick, int Velocity, int Sequence);
}
