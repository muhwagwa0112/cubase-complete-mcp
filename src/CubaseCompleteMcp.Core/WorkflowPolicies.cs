using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

/// <summary>Identity of a file bound into an approval so the commit can detect a change (TOCTOU).</summary>
public sealed record FileIdentity(string Path, bool Exists, long? Bytes, DateTimeOffset? LastWriteUtc, string? Sha256)
{
    public static FileIdentity Capture(string path, bool includeHash = true)
    {
        var full = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists) return new FileIdentity(full, false, null, null, null);
        string? hash = null;
        if (includeHash && info.Length <= 512L * 1024 * 1024)
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        return new FileIdentity(full, true, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), hash);
    }

    public JsonObject ToJson() => new()
    {
        ["path"] = Path, ["exists"] = Exists, ["bytes"] = Bytes,
        ["last_write_utc"] = LastWriteUtc?.ToString("O"), ["sha256"] = Sha256
    };
}

/// <summary>Validation for explicit absolute workflow paths (project and export files).</summary>
public static class WorkflowPathPolicy
{
    public static string RequireAbsolute(string? path, string extension, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new PlanException(ErrorCodes.InvalidArgument, "path is required.");
        if (!Path.IsPathFullyQualified(path)) throw new PlanException(ErrorCodes.InvalidArgument, "path must be an absolute path.");
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new PlanException(ErrorCodes.InvalidArgument, $"path must end with {extension}.");
        if (Path.GetFileNameWithoutExtension(full).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileNameWithoutExtension(full).Length == 0)
            throw new PlanException(ErrorCodes.InvalidArgument, "path has an invalid file name.");
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent))
            throw new PlanException(ErrorCodes.InvalidArgument, "The parent directory of path must already exist.");
        if (mustExist && !File.Exists(full)) throw new PlanException(ErrorCodes.InvalidArgument, $"The file does not exist: {full}");
        return full;
    }

    /// <summary>Applies the overwrite policy at planning time and returns the identity to bind into the plan.</summary>
    public static FileIdentity PlanOutput(string fullPath, bool overwrite)
    {
        var identity = FileIdentity.Capture(fullPath);
        if (identity.Exists && !overwrite)
            throw new PlanException(ErrorCodes.FileExists, $"The file already exists and overwrite=false: {fullPath}");
        return identity;
    }

    /// <summary>Re-checks an approved overwrite target immediately before dispatch.</summary>
    public static bool IsUnchanged(FileIdentity planned)
    {
        var current = FileIdentity.Capture(planned.Path, planned.Sha256 is not null);
        return current.Exists == planned.Exists && current.Bytes == planned.Bytes
            && current.LastWriteUtc == planned.LastWriteUtc && current.Sha256 == planned.Sha256;
    }
}

public sealed record WavValidation(bool Valid, string? Error, int Channels, int SampleRate, int BitsPerSample,
    long DataBytes, double DurationSeconds, string Format)
{
    public JsonObject ToJson() => new()
    {
        ["valid"] = Valid, ["error"] = Error, ["channels"] = Channels, ["sample_rate"] = SampleRate,
        ["bits_per_sample"] = BitsPerSample, ["data_bytes"] = DataBytes, ["duration_seconds"] = DurationSeconds,
        ["format"] = Format
    };
}

/// <summary>
/// RIFF/WAVE header and data check for the required mixdown profile (PCM, stereo, 48 kHz, 24-bit) and length.
/// A file that merely exists and stopped growing is not accepted.
/// </summary>
public static class WavFileValidator
{
    public static WavValidation Read(ReadOnlySpan<byte> bytes)
    {
        static WavValidation Fail(string error) => new(false, error, 0, 0, 0, 0, 0, "unknown");
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
            return Fail("not_riff_wave");
        var offset = 12;
        int channels = 0, rate = 0, bits = 0, blockAlign = 0;
        ushort formatTag = 0;
        long dataBytes = -1;
        var format = "unknown";
        while (offset + 8 <= bytes.Length)
        {
            var id = bytes.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var body = offset + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || body + 16 > bytes.Length) return Fail("fmt_chunk_truncated");
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(body + 4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 12, 2));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 14, 2));
                format = formatTag switch { 1 => "pcm", 3 => "ieee_float", 0xFFFE => "extensible", _ => $"tag_{formatTag}" };
                if (formatTag == 0xFFFE && size >= 40 && body + 40 <= bytes.Length)
                {
                    // WAVE_FORMAT_EXTENSIBLE: the first two bytes of the sub-format GUID carry the real format tag.
                    var subFormat = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 24, 2));
                    format = subFormat == 1 ? "pcm" : subFormat == 3 ? "ieee_float" : $"extensible_{subFormat}";
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataBytes = size;
                if (body + (long)size > bytes.Length) return Fail("data_chunk_truncated");
            }
            offset = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
            if (dataBytes >= 0 && formatTag != 0) break;
        }
        if (formatTag == 0) return Fail("fmt_chunk_missing");
        if (dataBytes < 0) return Fail("data_chunk_missing");
        if (blockAlign <= 0 || rate <= 0) return Fail("invalid_block_align_or_rate");
        var duration = dataBytes / (double)blockAlign / rate;
        return new WavValidation(true, null, channels, rate, bits, dataBytes, duration, format);
    }

    /// <summary>Checks the required profile and the requested length (tolerance: max(20 ms, stated host latency)).</summary>
    public static WavValidation Validate(ReadOnlySpan<byte> bytes, double expectedSeconds, double hostLatencySeconds = 0)
    {
        var read = Read(bytes);
        if (!read.Valid) return read;
        string? error = null;
        if (read.Format != "pcm") error = "format_not_pcm";
        else if (read.Channels != 2) error = "not_stereo";
        else if (read.SampleRate != 48000) error = "sample_rate_not_48000";
        else if (read.BitsPerSample != 24) error = "bit_depth_not_24";
        else if (read.DataBytes == 0) error = "empty_data";
        else if (Math.Abs(read.DurationSeconds - expectedSeconds) > Math.Max(0.020, hostLatencySeconds))
            error = "duration_out_of_tolerance";
        return read with { Valid = error is null, Error = error };
    }
}

public sealed record ImportTrackExpectation(int SourceIndex, string OriginalName, string ImportedName, int Channel,
    int EventCount);

public sealed record ImportExpectation(string VerificationTag, IReadOnlyList<ImportTrackExpectation> Tracks,
    bool ConductorSkipped, string ConductorReason, string SourceDigest);

public static class MidiVerificationLevels
{
    public const string Submitted = "submitted";
    public const string TargetsObserved = "targets_observed";
    public const string PartsObserved = "parts_observed";
    public const string ContentRoundtripVerified = "content_roundtrip_verified";
}

/// <summary>
/// Causal MIDI import planning. Each import gets a unique verification tag in the created track names (on a
/// temporary render copy only), so pre-existing tracks with the same name can never be adopted as this import's
/// result. Only a track 0 that contains no channel events in a multi-track file is treated as the conductor.
/// </summary>
public static class MidiImportCausality
{
    public static string VerificationTag(string operationId) => "CBMCP-" + operationId.Replace("-", "", StringComparison.Ordinal)[..8];

    public static bool IsConductor(MidiCompositionDocument document, int index)
    {
        if (index != 0 || document.Tracks.Count < 2) return false;
        var track = document.Tracks[0];
        return !track.Events.Concat(track.Parts.SelectMany(part => part.Events)).Any(IsChannelEvent);
    }

    public static bool IsChannelEvent(MidiCompositionEvent item) => item.Type.ToLowerInvariant() is "note" or "cc"
        or "control_change" or "program" or "program_change" or "pitch_bend" or "channel_pressure" or "poly_pressure";

    public static ImportExpectation Plan(MidiCompositionDocument document, string operationId)
    {
        var tag = VerificationTag(operationId);
        var conductor = IsConductor(document, 0);
        var tracks = new List<ImportTrackExpectation>();
        for (var index = conductor ? 1 : 0; index < document.Tracks.Count; index++)
        {
            var track = document.Tracks[index];
            var baseName = string.IsNullOrWhiteSpace(track.Name) ? $"Track {index + 1}" : track.Name.Trim();
            tracks.Add(new ImportTrackExpectation(index, track.Name, $"{baseName} [{tag}]", track.Channel,
                track.Events.Count + track.Parts.Sum(part => part.Events.Count)));
        }
        var reason = document.Tracks.Count < 2 ? "single_track_file_has_no_conductor"
            : conductor ? "track0_has_no_channel_events" : "track0_contains_channel_events";
        return new ImportExpectation(tag, tracks, conductor, reason, CanonicalDigest(document));
    }

    /// <summary>Returns a copy whose importable tracks carry the verification tag. The source document is untouched.</summary>
    public static MidiCompositionDocument TaggedCopy(MidiCompositionDocument document, ImportExpectation expectation)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(document);
        var copy = System.Text.Json.JsonSerializer.Deserialize<MidiCompositionDocument>(json)!;
        foreach (var track in expectation.Tracks) copy.Tracks[track.SourceIndex].Name = track.ImportedName;
        return copy;
    }

    /// <summary>Order-independent digest of channel and map events per track (names and host metadata excluded).</summary>
    public static string CanonicalDigest(MidiCompositionDocument document) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
            CanonicalEvents(document).Select(line => line))))).ToLowerInvariant();

    public static IReadOnlyList<string> CanonicalEvents(MidiCompositionDocument document)
    {
        var lines = new List<string>();
        var ordinal = 0;
        foreach (var track in document.Tracks)
        {
            var events = track.Events.Select(item => (item, offset: 0L))
                .Concat(track.Parts.SelectMany(part => part.Events.Select(item => (item, offset: part.StartTick))))
                .Where(pair => IsChannelEvent(pair.item) || pair.item.Type.ToLowerInvariant() is "tempo" or "time_signature")
                .Select(pair => Canonical(pair.item, pair.offset, track.Channel, document.Ppq))
                .OrderBy(line => line, StringComparer.Ordinal).ToArray();
            if (events.Length == 0) continue;
            lines.Add($"track#{ordinal++}");
            lines.AddRange(events);
        }
        return lines;
    }

    /// <summary>Compares note/CC/timing content of a source document and a file exported back from Cubase.</summary>
    public static JsonObject Compare(MidiCompositionDocument source, MidiCompositionDocument exported)
    {
        var expected = CanonicalEvents(source).Where(line => !line.StartsWith("track#", StringComparison.Ordinal)
            && !line.StartsWith("tempo", StringComparison.Ordinal) && !line.StartsWith("time_signature", StringComparison.Ordinal))
            .OrderBy(line => line, StringComparer.Ordinal).ToArray();
        var actual = CanonicalEvents(exported).Where(line => !line.StartsWith("track#", StringComparison.Ordinal)
            && !line.StartsWith("tempo", StringComparison.Ordinal) && !line.StartsWith("time_signature", StringComparison.Ordinal))
            .OrderBy(line => line, StringComparer.Ordinal).ToArray();
        var missing = expected.Except(actual, StringComparer.Ordinal).Take(20).ToArray();
        var extra = actual.Except(expected, StringComparer.Ordinal).Take(20).ToArray();
        var equal = expected.SequenceEqual(actual, StringComparer.Ordinal);
        return new JsonObject
        {
            ["content_equal"] = equal,
            ["level"] = equal ? MidiVerificationLevels.ContentRoundtripVerified : MidiVerificationLevels.PartsObserved,
            ["expected_event_count"] = expected.Length, ["actual_event_count"] = actual.Length,
            ["missing_examples"] = new JsonArray(missing.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            ["unexpected_examples"] = new JsonArray(extra.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            ["normalization"] = "track names, text/meta events and end-of-track are excluded; ticks are scaled to 480 PPQ"
        };
    }

    private static string Canonical(MidiCompositionEvent item, long offset, int trackChannel, int ppq)
    {
        long Scale(long tick) => (long)Math.Round(tick * 480.0 / ppq);
        var channel = item.Channel ?? trackChannel;
        var tick = Scale(item.Tick + offset);
        return item.Type.ToLowerInvariant() switch
        {
            "note" => $"note t={tick} d={Scale(item.DurationTicks)} ch={channel} n={item.Note} v={item.Velocity}",
            "cc" or "control_change" => $"cc t={tick} ch={channel} c={item.Controller} v={item.Value}",
            "program" or "program_change" => $"program t={tick} ch={channel} p={item.Program}",
            "pitch_bend" => $"pitch_bend t={tick} ch={channel} b={item.Bend}",
            "channel_pressure" => $"channel_pressure t={tick} ch={channel} p={item.Pressure}",
            "poly_pressure" => $"poly_pressure t={tick} ch={channel} n={item.Note} p={item.Pressure}",
            "tempo" => $"tempo t={tick} bpm={item.Bpm:0.###}",
            "time_signature" => $"time_signature t={tick} {item.Numerator}/{item.Denominator}",
            _ => $"{item.Type} t={tick}"
        };
    }
}
