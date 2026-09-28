using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

public sealed class MidiCompositionDocument
{
    public int SchemaVersion { get; set; } = 1;
    public required string Id { get; set; }
    public required string Name { get; set; }
    public int Ppq { get; set; } = 480;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<MidiCompositionTrack> Tracks { get; set; } = [];
}

public sealed class MidiCompositionTrack
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public int Channel { get; set; }
    public List<MidiCompositionPart> Parts { get; set; } = [];
    public List<MidiCompositionEvent> Events { get; set; } = [];
}

public sealed class MidiCompositionPart
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public long StartTick { get; set; }
    public long LengthTicks { get; set; }
    public List<MidiCompositionEvent> Events { get; set; } = [];
}

public sealed class MidiCompositionEvent
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public long Tick { get; set; }
    public long DurationTicks { get; set; }
    public int? Channel { get; set; }
    public int? Note { get; set; }
    public int? Velocity { get; set; }
    public int? ReleaseVelocity { get; set; }
    public int? Controller { get; set; }
    public int? Value { get; set; }
    public int? Program { get; set; }
    public int? Bend { get; set; }
    public int? Pressure { get; set; }
    public double? Bpm { get; set; }
    public int? Numerator { get; set; }
    public int? Denominator { get; set; }
    public int? KeySharpsFlats { get; set; }
    public bool? Minor { get; set; }
    public string? Text { get; set; }
    public int? MetaType { get; set; }
    public string? DataHex { get; set; }
}

public sealed class MidiCompositionService
{
    public const int MaximumTracks = 256;
    public const int MaximumEvents = 1_000_000;
    public const long MaximumTick = 0x0fffffff;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    public MidiCompositionService(string localStateRoot)
    {
        _root = Path.Combine(Path.GetFullPath(localStateRoot), "compositions");
        Directory.CreateDirectory(_root);
    }

    public JsonObject Execute(string action, JsonObject arguments, string clientSessionId)
    {
        var normalized = action.Trim().ToLowerInvariant();
        return normalized switch
        {
            "create" or "create_document" => Create(arguments, clientSessionId),
            "list" or "list_documents" => List(clientSessionId),
            "get" or "get_document" => Get(arguments, clientSessionId),
            "delete" or "delete_document" => Delete(arguments, clientSessionId),
            "add_track" => AddTrack(arguments, clientSessionId),
            "remove_track" => RemoveTrack(arguments, clientSessionId),
            "rename_track" => RenameTrack(arguments, clientSessionId),
            "create_part" => CreatePart(arguments, clientSessionId),
            "remove_part" => RemovePart(arguments, clientSessionId),
            "add_notes" => AddNotes(arguments, clientSessionId),
            "add_chords" => AddChords(arguments, clientSessionId),
            "add_drum_pattern" => AddDrumPattern(arguments, clientSessionId),
            "add_cc" => AddTypedEvents(arguments, clientSessionId, "cc"),
            "add_pitch_bend" => AddTypedEvents(arguments, clientSessionId, "pitch_bend"),
            "add_channel_pressure" => AddTypedEvents(arguments, clientSessionId, "channel_pressure"),
            "add_poly_pressure" => AddTypedEvents(arguments, clientSessionId, "poly_pressure"),
            "add_program_change" => AddTypedEvents(arguments, clientSessionId, "program_change"),
            "add_tempo" => AddTypedEvents(arguments, clientSessionId, "tempo"),
            "add_time_signature" => AddTypedEvents(arguments, clientSessionId, "time_signature"),
            "add_key_signature" => AddTypedEvents(arguments, clientSessionId, "key_signature"),
            "add_marker" => AddTypedEvents(arguments, clientSessionId, "marker"),
            "add_lyrics" => AddTypedEvents(arguments, clientSessionId, "lyric"),
            "add_events" => AddTypedEvents(arguments, clientSessionId, null),
            "quantize" => Transform(arguments, clientSessionId, Quantize),
            "transpose" => Transform(arguments, clientSessionId, Transpose),
            "humanize" => Transform(arguments, clientSessionId, Humanize),
            "replace_range" => ReplaceRange(arguments, clientSessionId),
            "render" or "render_file" or "export_file" => Render(arguments, clientSessionId),
            "inspect_file" => InspectFile(arguments),
            "compare_file" => CompareFile(arguments, clientSessionId),
            "import_file" => ImportFile(arguments, clientSessionId),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported MIDI composition action.")
        };
    }

    public (MidiCompositionDocument Document, string Path, byte[] Bytes) RenderDocument(
        string documentId, string clientSessionId, string? requestedPath = null, bool overwrite = false)
    {
        var document = Load(documentId, clientSessionId);
        var bytes = StandardMidiFile.Write(document);
        var path = requestedPath is null
            ? Path.Combine(SessionRoot(clientSessionId), "renders", SafeFileName(document.Name) + "-" + document.Id + ".mid")
            : ValidateMidiPath(requestedPath, requireExisting: false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && !overwrite && requestedPath is not null)
            throw new IOException($"The MIDI file already exists: {path}. Set overwrite=true through the confirmation flow.");
        WriteAtomic(path, bytes);
        return (document, path, bytes);
    }

    private JsonObject Create(JsonObject arguments, string session)
    {
        var ppq = arguments["ppq"]?.GetValue<int>() ?? 480;
        if (ppq is < 24 or > 32767) throw new ArgumentOutOfRangeException(nameof(arguments), "ppq must be between 24 and 32767.");
        var now = DateTimeOffset.UtcNow;
        var document = new MidiCompositionDocument
        {
            Id = NewId("midi"), Name = RequiredOrDefault(arguments, "name", "Untitled MIDI"), Ppq = ppq,
            CreatedAt = now, ModifiedAt = now,
            Tracks = [new MidiCompositionTrack { Id = NewId("trk"), Name = "Conductor", Channel = 0 }]
        };
        var tempo = arguments["tempo_bpm"]?.GetValue<double>() ?? 120;
        var numerator = arguments["numerator"]?.GetValue<int>() ?? 4;
        var denominator = arguments["denominator"]?.GetValue<int>() ?? 4;
        document.Tracks[0].Events.Add(NormalizeEvent(new JsonObject { ["type"] = "tempo", ["tick"] = 0, ["bpm"] = tempo }, null));
        document.Tracks[0].Events.Add(NormalizeEvent(new JsonObject
        {
            ["type"] = "time_signature", ["tick"] = 0, ["numerator"] = numerator, ["denominator"] = denominator
        }, null));
        Save(document, session);
        return DocumentReply(document, session, "created");
    }

    private JsonObject List(string session)
    {
        var root = SessionRoot(session);
        Directory.CreateDirectory(root);
        var documents = new JsonArray();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = JsonSerializer.Deserialize<MidiCompositionDocument>(File.ReadAllBytes(path), PipeProtocol.JsonOptions);
                if (document is not null) documents.Add(Summary(document));
            }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
        return new JsonObject { ["documents"] = documents, ["count"] = documents.Count, ["session_scoped"] = true };
    }

    private JsonObject Get(JsonObject arguments, string session) =>
        DocumentReply(Load(RequireString(arguments, "document_id"), session), session, "loaded");

    private JsonObject Delete(JsonObject arguments, string session)
    {
        var id = RequireDocumentId(arguments);
        var path = DocumentPath(id, session);
        lock (LockFor(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("MIDI composition document was not found.", path);
            File.Delete(path);
        }
        return new JsonObject { ["document_id"] = id, ["deleted"] = true, ["verified"] = !File.Exists(path) };
    }

    private JsonObject AddTrack(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        if (document.Tracks.Count >= MaximumTracks) throw new InvalidOperationException($"A document cannot exceed {MaximumTracks} tracks.");
        var channel = arguments["channel"]?.GetValue<int>() ?? Math.Clamp(document.Tracks.Count - 1, 0, 15);
        ValidateRange(channel, 0, 15, "channel");
        var track = new MidiCompositionTrack { Id = NewId("trk"), Name = RequiredOrDefault(arguments, "name", $"Track {document.Tracks.Count}"), Channel = channel };
        document.Tracks.Add(track);
        Save(document, session);
        return MutationReply(document, track.Id, "track_added");
    }

    private JsonObject RemoveTrack(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var track = FindTrack(document, arguments);
        if (document.Tracks.Count == 1) throw new InvalidOperationException("The conductor track cannot be the only track removed.");
        document.Tracks.Remove(track);
        Save(document, session);
        return MutationReply(document, track.Id, "track_removed");
    }

    private JsonObject RenameTrack(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var track = FindTrack(document, arguments);
        track.Name = RequireString(arguments, "name");
        Save(document, session);
        return MutationReply(document, track.Id, "track_renamed");
    }

    private JsonObject CreatePart(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var track = FindTrack(document, arguments);
        var start = GetTick(arguments, "start_tick", 0);
        var length = GetTick(arguments, "length_ticks", document.Ppq * 4L);
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(arguments), "length_ticks must be positive.");
        var part = new MidiCompositionPart { Id = NewId("part"), Name = RequiredOrDefault(arguments, "name", "MIDI Part"), StartTick = start, LengthTicks = length };
        track.Parts.Add(part);
        Save(document, session);
        return MutationReply(document, part.Id, "part_created");
    }

    private JsonObject RemovePart(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var track = FindTrack(document, arguments);
        var part = FindPart(track, arguments);
        track.Parts.Remove(part);
        Save(document, session);
        return MutationReply(document, part.Id, "part_removed");
    }

    private JsonObject AddNotes(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var (track, target) = ResolveTarget(document, arguments);
        var source = RequireArray(arguments, "events");
        foreach (var item in source)
        {
            var node = item?.DeepClone().AsObject() ?? throw new ArgumentException("Every note must be an object.");
            node["type"] = "note";
            target.Add(NormalizeEvent(node, track.Channel));
        }
        ValidateDocument(document);
        Save(document, session);
        return MutationReply(document, null, "notes_added", source.Count);
    }

    private JsonObject AddChords(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var (track, target) = ResolveTarget(document, arguments);
        var source = RequireArray(arguments, "events");
        var added = 0;
        foreach (var item in source)
        {
            if (item is not JsonObject chord || chord["notes"] is not JsonArray notes || notes.Count == 0)
                throw new ArgumentException("Every chord requires a non-empty notes array.");
            foreach (var note in notes)
            {
                var node = new JsonObject
                {
                    ["type"] = "note", ["tick"] = chord["tick"]?.DeepClone(), ["duration_ticks"] = chord["duration_ticks"]?.DeepClone(),
                    ["note"] = note?.DeepClone(), ["velocity"] = chord["velocity"]?.DeepClone(), ["release_velocity"] = chord["release_velocity"]?.DeepClone(),
                    ["channel"] = chord["channel"]?.DeepClone()
                };
                target.Add(NormalizeEvent(node, track.Channel));
                added++;
            }
        }
        ValidateDocument(document);
        Save(document, session);
        return MutationReply(document, null, "chords_added", added);
    }

    private JsonObject AddDrumPattern(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var (track, target) = ResolveTarget(document, arguments);
        var lanes = RequireArray(arguments, "events");
        var added = 0;
        foreach (var item in lanes)
        {
            if (item is not JsonObject lane || lane["steps"] is not JsonArray steps)
                throw new ArgumentException("Every drum lane requires note and steps.");
            var stepTicks = ReadLong(lane["step_ticks"]) ?? document.Ppq / 4L;
            if (stepTicks <= 0) throw new ArgumentOutOfRangeException(nameof(arguments), "step_ticks must be positive.");
            var baseTick = ReadLong(lane["tick"]) ?? 0;
            foreach (var stepNode in steps)
            {
                var step = stepNode?.GetValue<int>() ?? throw new ArgumentException("Drum steps must be integers.");
                var note = new JsonObject
                {
                    ["type"] = "note", ["tick"] = checked(baseTick + step * stepTicks), ["duration_ticks"] = lane["duration_ticks"]?.DeepClone() ?? Math.Max(1, stepTicks / 2),
                    ["note"] = lane["note"]?.DeepClone(), ["velocity"] = lane["velocity"]?.DeepClone(), ["channel"] = lane["channel"]?.DeepClone() ?? 9
                };
                target.Add(NormalizeEvent(note, track.Channel));
                added++;
            }
        }
        ValidateDocument(document);
        Save(document, session);
        return MutationReply(document, null, "drum_pattern_added", added);
    }

    private JsonObject AddTypedEvents(JsonObject arguments, string session, string? forcedType)
    {
        var document = LoadForUpdate(arguments, session);
        var (track, target) = ResolveTarget(document, arguments);
        var source = RequireArray(arguments, "events");
        foreach (var item in source)
        {
            var node = item?.DeepClone().AsObject() ?? throw new ArgumentException("Every MIDI event must be an object.");
            if (forcedType is not null) node["type"] = forcedType;
            target.Add(NormalizeEvent(node, track.Channel));
        }
        ValidateDocument(document);
        Save(document, session);
        return MutationReply(document, null, forcedType is null ? "events_added" : forcedType + "_added", source.Count);
    }

    private delegate int TransformOperation(MidiCompositionDocument document, MidiCompositionTrack track,
        MidiCompositionPart? part, JsonObject arguments);

    private JsonObject Transform(JsonObject arguments, string session, TransformOperation operation)
    {
        var document = LoadForUpdate(arguments, session);
        var track = FindTrack(document, arguments);
        var part = arguments["part_id"] is null ? null : FindPart(track, arguments);
        var changed = operation(document, track, part, arguments);
        ValidateDocument(document);
        Save(document, session);
        return MutationReply(document, part?.Id ?? track.Id, "transformed", changed);
    }

    private static int Quantize(MidiCompositionDocument document, MidiCompositionTrack track, MidiCompositionPart? part, JsonObject arguments)
    {
        var grid = ReadLong(arguments["grid_ticks"]) ?? document.Ppq / 4L;
        var strength = arguments["strength"]?.GetValue<double>() ?? 1;
        var swing = arguments["swing"]?.GetValue<double>() ?? 0;
        if (grid <= 0) throw new ArgumentOutOfRangeException(nameof(arguments), "grid_ticks must be positive.");
        if (strength is < 0 or > 1 || swing is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(arguments), "strength must be 0..1 and swing -1..1.");
        var changed = 0;
        foreach (var item in SelectEvents(track, part, arguments).Where(item => item.Type == "note"))
        {
            var index = (long)Math.Round(item.Tick / (double)grid, MidpointRounding.AwayFromZero);
            var target = checked(index * grid);
            if ((index & 1) == 1) target = checked(target + (long)Math.Round(grid * 0.5 * swing));
            var quantized = Math.Clamp(item.Tick + (long)Math.Round((target - item.Tick) * strength), 0, MaximumTick);
            if (quantized != item.Tick) { item.Tick = quantized; changed++; }
            if (arguments["quantize_duration"]?.GetValue<bool>() == true)
                item.DurationTicks = Math.Max(1, (long)Math.Round(item.DurationTicks / (double)grid) * grid);
        }
        return changed;
    }

    private static int Transpose(MidiCompositionDocument document, MidiCompositionTrack track, MidiCompositionPart? part, JsonObject arguments)
    {
        _ = document;
        var semitones = arguments["semitones"]?.GetValue<int>() ?? throw new ArgumentException("semitones is required.");
        var changed = 0;
        foreach (var item in SelectEvents(track, part, arguments).Where(item => item.Note is not null && item.Type is "note" or "poly_pressure"))
        {
            item.Note = Math.Clamp(item.Note!.Value + semitones, 0, 127);
            changed++;
        }
        return changed;
    }

    private static int Humanize(MidiCompositionDocument document, MidiCompositionTrack track, MidiCompositionPart? part, JsonObject arguments)
    {
        _ = document;
        var timing = Math.Clamp(arguments["timing_ticks"]?.GetValue<int>() ?? 0, 0, 9600);
        var velocity = Math.Clamp(arguments["velocity"]?.GetValue<int>() ?? 0, 0, 127);
        var seed = arguments["seed"]?.GetValue<int>() ?? 0;
        var random = new Random(seed);
        var changed = 0;
        foreach (var item in SelectEvents(track, part, arguments).Where(item => item.Type == "note"))
        {
            if (timing > 0) item.Tick = Math.Clamp(item.Tick + random.Next(-timing, timing + 1), 0, MaximumTick);
            if (velocity > 0) item.Velocity = Math.Clamp((item.Velocity ?? 100) + random.Next(-velocity, velocity + 1), 1, 127);
            changed++;
        }
        return changed;
    }

    private JsonObject ReplaceRange(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var (track, target) = ResolveTarget(document, arguments);
        var start = GetTick(arguments, "start_tick", 0);
        var end = GetTick(arguments, "end_tick", MaximumTick);
        if (end <= start) throw new ArgumentException("end_tick must be greater than start_tick.");
        var removed = target.RemoveAll(item => item.Tick >= start && item.Tick < end);
        var source = arguments["events"] as JsonArray ?? [];
        foreach (var item in source)
            target.Add(NormalizeEvent(item?.DeepClone().AsObject() ?? throw new ArgumentException("Every replacement event must be an object."), track.Channel));
        ValidateDocument(document);
        Save(document, session);
        var reply = MutationReply(document, null, "range_replaced", source.Count);
        reply["removed"] = removed;
        return reply;
    }

    private JsonObject Render(JsonObject arguments, string session)
    {
        var id = RequireDocumentId(arguments);
        var path = arguments["path"]?.GetValue<string>();
        var overwrite = arguments["overwrite"]?.GetValue<bool>() ?? false;
        var result = RenderDocument(id, session, path, overwrite);
        return new JsonObject
        {
            ["document"] = Summary(result.Document), ["path"] = result.Path, ["bytes"] = result.Bytes.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(result.Bytes)).ToLowerInvariant(), ["verified"] = File.Exists(result.Path)
        };
    }

    private static JsonObject InspectFile(JsonObject arguments)
    {
        var path = ValidateMidiPath(RequireString(arguments, "path"), requireExisting: true);
        var bytes = File.ReadAllBytes(path);
        var document = StandardMidiFile.Read(bytes, Path.GetFileNameWithoutExtension(path));
        return new JsonObject
        {
            ["path"] = path, ["bytes"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ["document"] = JsonSerializer.SerializeToNode(document, PipeProtocol.JsonOptions), ["summary"] = Summary(document), ["verified"] = true
        };
    }

    private JsonObject ImportFile(JsonObject arguments, string session)
    {
        var path = ValidateMidiPath(RequireString(arguments, "path"), requireExisting: true);
        var bytes = File.ReadAllBytes(path);
        var document = StandardMidiFile.Read(bytes, Path.GetFileNameWithoutExtension(path));
        document.Id = NewId("midi");
        document.Name = arguments["name"]?.GetValue<string>() ?? document.Name;
        document.CreatedAt = document.ModifiedAt = DateTimeOffset.UtcNow;
        Save(document, session);
        var reply = DocumentReply(document, session, "imported");
        reply["source_path"] = path;
        reply["source_sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return reply;
    }

    public MidiCompositionDocument LoadDocument(string documentId, string clientSessionId) => Load(documentId, clientSessionId);

    /// <summary>Writes a render of a transient (not stored) document copy, e.g. a verification-tagged import file.</summary>
    public (string Path, byte[] Bytes) RenderTransient(MidiCompositionDocument document, string clientSessionId, string suffix)
    {
        var bytes = StandardMidiFile.Write(document);
        var path = Path.Combine(SessionRoot(clientSessionId), "renders",
            SafeFileName(document.Name) + "-" + document.Id + "-" + SafeFileName(suffix) + ".mid");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomic(path, bytes);
        return (path, bytes);
    }

    /// <summary>
    /// Compares a stored document with a Standard MIDI File exported back from Cubase (the official export path),
    /// using canonical note/CC/timing content. This is the content_roundtrip_verified evidence for an import.
    /// </summary>
    private JsonObject CompareFile(JsonObject arguments, string session)
    {
        var document = LoadForUpdate(arguments, session);
        var path = ValidateMidiPath(RequireString(arguments, "path"), requireExisting: true);
        var bytes = File.ReadAllBytes(path);
        var exported = StandardMidiFile.Read(bytes, Path.GetFileNameWithoutExtension(path));
        var comparison = MidiImportCausality.Compare(document, exported);
        comparison["document_id"] = document.Id;
        comparison["exported_path"] = path;
        comparison["exported_sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        comparison["source_sha256"] = Convert.ToHexString(SHA256.HashData(StandardMidiFile.Write(document))).ToLowerInvariant();
        comparison["source_digest"] = MidiImportCausality.CanonicalDigest(document);
        return comparison;
    }

    private MidiCompositionDocument LoadForUpdate(JsonObject arguments, string session) => Load(RequireDocumentId(arguments), session);

    private MidiCompositionDocument Load(string id, string session)
    {
        var path = DocumentPath(ValidateId(id, "midi_"), session);
        lock (LockFor(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("MIDI composition document was not found in this client session.", path);
            var document = JsonSerializer.Deserialize<MidiCompositionDocument>(File.ReadAllBytes(path), PipeProtocol.JsonOptions)
                ?? throw new InvalidDataException("MIDI composition document deserialized to null.");
            ValidateDocument(document);
            return document;
        }
    }

    private void Save(MidiCompositionDocument document, string session)
    {
        ValidateDocument(document);
        document.ModifiedAt = DateTimeOffset.UtcNow;
        var path = DocumentPath(document.Id, session);
        lock (LockFor(path)) WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(document, PipeProtocol.JsonOptions));
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string SessionRoot(string session)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session))).ToLowerInvariant()[..24];
        return Path.Combine(_root, hash);
    }

    private string DocumentPath(string id, string session)
    {
        var root = SessionRoot(session);
        Directory.CreateDirectory(root);
        var path = Path.GetFullPath(Path.Combine(root, id + ".json"));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Document path escaped the session root.");
        return path;
    }

    private object LockFor(string path) => _locks.GetOrAdd(path, static _ => new object());

    private static (MidiCompositionTrack Track, List<MidiCompositionEvent> Target) ResolveTarget(
        MidiCompositionDocument document, JsonObject arguments)
    {
        var track = FindTrack(document, arguments);
        return arguments["part_id"] is null
            ? (track, track.Events)
            : (track, FindPart(track, arguments).Events);
    }

    private static MidiCompositionTrack FindTrack(MidiCompositionDocument document, JsonObject arguments)
    {
        var id = RequireString(arguments, "track_id");
        return document.Tracks.SingleOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown track_id '{id}'.");
    }

    private static MidiCompositionPart FindPart(MidiCompositionTrack track, JsonObject arguments)
    {
        var id = RequireString(arguments, "part_id");
        return track.Parts.SingleOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown part_id '{id}'.");
    }

    private static IEnumerable<MidiCompositionEvent> SelectEvents(MidiCompositionTrack track, MidiCompositionPart? part, JsonObject arguments)
    {
        var source = part?.Events ?? track.Events;
        var start = ReadLong(arguments["start_tick"]) ?? 0;
        var end = ReadLong(arguments["end_tick"]) ?? MaximumTick;
        return source.Where(item => item.Tick >= start && item.Tick < end).ToArray();
    }

    private static MidiCompositionEvent NormalizeEvent(JsonObject node, int? defaultChannel)
    {
        var type = RequireString(node, "type").Trim().ToLowerInvariant();
        var item = new MidiCompositionEvent
        {
            Id = node["id"]?.GetValue<string>() ?? NewId("evt"), Type = type,
            Tick = GetTick(node, "tick", 0), DurationTicks = ReadLong(node["duration_ticks"]) ?? 0,
            Channel = node["channel"]?.GetValue<int>() ?? defaultChannel,
            Note = node["note"]?.GetValue<int>(), Velocity = node["velocity"]?.GetValue<int>(),
            ReleaseVelocity = node["release_velocity"]?.GetValue<int>(), Controller = node["controller"]?.GetValue<int>(),
            Value = node["value"]?.GetValue<int>(), Program = node["program"]?.GetValue<int>(), Bend = node["bend"]?.GetValue<int>(),
            Pressure = node["pressure"]?.GetValue<int>(), Bpm = node["bpm"]?.GetValue<double>(),
            Numerator = node["numerator"]?.GetValue<int>(), Denominator = node["denominator"]?.GetValue<int>(),
            KeySharpsFlats = node["key_sharps_flats"]?.GetValue<int>(), Minor = node["minor"]?.GetValue<bool>(),
            Text = node["text"]?.GetValue<string>(), MetaType = node["meta_type"]?.GetValue<int>(), DataHex = node["data_hex"]?.GetValue<string>()
        };
        ValidateEvent(item);
        return item;
    }

    private static void ValidateDocument(MidiCompositionDocument document)
    {
        if (document.SchemaVersion != 1 || !document.Id.StartsWith("midi_", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported MIDI composition document schema or ID.");
        if (document.Ppq is < 24 or > 32767) throw new InvalidDataException("Document PPQ is invalid.");
        if (document.Tracks.Count is 0 or > MaximumTracks) throw new InvalidDataException("Document track count is invalid.");
        var eventCount = 0;
        foreach (var track in document.Tracks)
        {
            ValidateRange(track.Channel, 0, 15, "track.channel");
            foreach (var item in track.Events) { ValidateEvent(item); eventCount++; }
            foreach (var part in track.Parts)
            {
                if (part.StartTick < 0 || part.LengthTicks <= 0 || part.StartTick + part.LengthTicks > MaximumTick)
                    throw new InvalidDataException("MIDI part range is invalid.");
                foreach (var item in part.Events)
                {
                    ValidateEvent(item);
                    var eventEnd = item.Tick + (item.Type == "note" ? item.DurationTicks : 0);
                    if (eventEnd > part.LengthTicks) throw new InvalidDataException("A MIDI event extends beyond its part range.");
                    eventCount++;
                }
            }
        }
        if (eventCount > MaximumEvents) throw new InvalidDataException($"A document cannot exceed {MaximumEvents} events.");
    }

    private static void ValidateEvent(MidiCompositionEvent item)
    {
        if (item.Tick is < 0 or > MaximumTick) throw new InvalidDataException("MIDI event tick is outside the supported range.");
        if (item.Channel is { } channel) ValidateRange(channel, 0, 15, "channel");
        switch (item.Type)
        {
            case "note":
                RequiredRange(item.Note, 0, 127, "note"); RequiredRange(item.Velocity ?? 100, 1, 127, "velocity");
                RequiredRange(item.ReleaseVelocity ?? 64, 0, 127, "release_velocity");
                if (item.DurationTicks <= 0 || item.Tick + item.DurationTicks > MaximumTick) throw new InvalidDataException("Note duration is invalid.");
                item.Velocity ??= 100; item.ReleaseVelocity ??= 64; break;
            case "cc": RequiredRange(item.Controller, 0, 127, "controller"); RequiredRange(item.Value, 0, 127, "value"); break;
            case "program_change": RequiredRange(item.Program, 0, 127, "program"); break;
            case "pitch_bend": RequiredRange(item.Bend, -8192, 8191, "bend"); break;
            case "channel_pressure": RequiredRange(item.Pressure, 0, 127, "pressure"); break;
            case "poly_pressure": RequiredRange(item.Note, 0, 127, "note"); RequiredRange(item.Pressure, 0, 127, "pressure"); break;
            case "tempo": if (item.Bpm is null || !double.IsFinite(item.Bpm.Value) || item.Bpm is < 1 or > 1000) throw new InvalidDataException("bpm must be between 1 and 1000."); break;
            case "time_signature": RequiredRange(item.Numerator, 1, 255, "numerator"); if (item.Denominator is null || item.Denominator < 1 || item.Denominator > 128 || (item.Denominator.Value & (item.Denominator.Value - 1)) != 0) throw new InvalidDataException("denominator must be a power of two from 1 to 128."); break;
            case "key_signature": RequiredRange(item.KeySharpsFlats, -7, 7, "key_sharps_flats"); item.Minor ??= false; break;
            case "marker" or "lyric" or "text": if (item.Text is null || item.Text.Length > 4096) throw new InvalidDataException("Text MIDI events require text of at most 4096 characters."); break;
            case "sysex": _ = ParseHex(item.DataHex ?? throw new InvalidDataException("SysEx requires data_hex.")); break;
            case "meta":
                RequiredRange(item.MetaType, 0, 127, "meta_type");
                if (item.MetaType == 0x2F)
                    throw new InvalidDataException("meta_type 0x2F (End of Track) is generated by the SMF writer and cannot be added explicitly.");
                _ = ParseHex(item.DataHex ?? string.Empty);
                break;
            default: throw new InvalidDataException($"Unsupported MIDI event type '{item.Type}'.");
        }
    }

    internal static byte[] ParseHex(string value)
    {
        var compact = new string(value.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray());
        if ((compact.Length & 1) != 0) throw new InvalidDataException("Hex data must contain complete bytes.");
        try { return Convert.FromHexString(compact); }
        catch (FormatException error) { throw new InvalidDataException("Hex data contains an invalid character.", error); }
    }

    private static JsonObject DocumentReply(MidiCompositionDocument document, string session, string action) => new()
    {
        ["action"] = action, ["document"] = JsonSerializer.SerializeToNode(document, PipeProtocol.JsonOptions),
        ["summary"] = Summary(document), ["session_scoped"] = true,
        ["storage_scope"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session))).ToLowerInvariant()[..12]
    };

    private static JsonObject MutationReply(MidiCompositionDocument document, string? targetId, string action, int? affected = null) => new()
    {
        ["action"] = action, ["document_id"] = document.Id, ["target_id"] = targetId,
        ["affected_events"] = affected, ["summary"] = Summary(document), ["verified"] = true
    };

    public static JsonObject Summary(MidiCompositionDocument document)
    {
        var events = document.Tracks.Sum(track => track.Events.Count + track.Parts.Sum(part => part.Events.Count));
        var notes = document.Tracks.Sum(track => track.Events.Count(item => item.Type == "note")
            + track.Parts.Sum(part => part.Events.Count(item => item.Type == "note")));
        var lastTick = document.Tracks.SelectMany(track => track.Events.Concat(track.Parts.SelectMany(part =>
                part.Events.Select(item => CloneWithTick(item, checked(item.Tick + part.StartTick))))))
            .Select(item => item.Tick + (item.Type == "note" ? item.DurationTicks : 0)).DefaultIfEmpty(0).Max();
        return new JsonObject
        {
            ["id"] = document.Id, ["name"] = document.Name, ["ppq"] = document.Ppq, ["tracks"] = document.Tracks.Count,
            ["parts"] = document.Tracks.Sum(track => track.Parts.Count), ["events"] = events, ["notes"] = notes,
            ["last_tick"] = lastTick, ["modified_at"] = document.ModifiedAt
        };
    }

    private static MidiCompositionEvent CloneWithTick(MidiCompositionEvent item, long tick) => new()
    {
        Id = item.Id, Type = item.Type, Tick = tick, DurationTicks = item.DurationTicks, Channel = item.Channel,
        Note = item.Note, Velocity = item.Velocity, ReleaseVelocity = item.ReleaseVelocity, Controller = item.Controller,
        Value = item.Value, Program = item.Program, Bend = item.Bend, Pressure = item.Pressure, Bpm = item.Bpm,
        Numerator = item.Numerator, Denominator = item.Denominator, KeySharpsFlats = item.KeySharpsFlats,
        Minor = item.Minor, Text = item.Text, MetaType = item.MetaType, DataHex = item.DataHex
    };

    private static string RequireDocumentId(JsonObject arguments) => ValidateId(RequireString(arguments, "document_id"), "midi_");
    private static string ValidateId(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length <= 80 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        ? value : throw new ArgumentException($"Invalid {prefix.TrimEnd('_')} ID.");
    private static string NewId(string prefix) => prefix + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
    private static string RequireString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() is { Length: > 0 } value ? value : throw new ArgumentException($"{name} is required.");
    private static string RequiredOrDefault(JsonObject arguments, string name, string fallback) =>
        arguments[name]?.GetValue<string>() is { Length: > 0 } value ? value : fallback;
    private static JsonArray RequireArray(JsonObject arguments, string name) =>
        arguments[name] as JsonArray ?? throw new ArgumentException($"{name} must be an array.");
    private static long GetTick(JsonObject arguments, string name, long fallback)
    {
        var value = ReadLong(arguments[name]) ?? fallback;
        if (value is < 0 or > MaximumTick) throw new ArgumentOutOfRangeException(nameof(arguments), $"{name} must be between 0 and {MaximumTick}.");
        return value;
    }
    private static long? ReadLong(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonValue value) throw new ArgumentException("Expected an integer JSON value.");
        if (value.TryGetValue<long>(out var longValue)) return longValue;
        if (value.TryGetValue<int>(out var intValue)) return intValue;
        if (value.TryGetValue<uint>(out var uintValue)) return uintValue;
        if (value.TryGetValue<ulong>(out var ulongValue) && ulongValue <= long.MaxValue) return (long)ulongValue;
        if (value.TryGetValue<JsonElement>(out var element) && element.TryGetInt64(out var elementValue)) return elementValue;
        throw new ArgumentException("Expected an integer JSON value.");
    }
    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum) throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }
    private static void RequiredRange(int? value, int minimum, int maximum, string name)
    {
        if (value is null) throw new InvalidDataException($"{name} is required.");
        ValidateRange(value.Value, minimum, maximum, name);
    }
    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "composition" : result[..Math.Min(80, result.Length)];
    }
    private static string ValidateMidiPath(string value, bool requireExisting)
    {
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("MIDI path must be absolute.");
        var path = Path.GetFullPath(value);
        if (!Path.GetExtension(path).Equals(".mid", StringComparison.OrdinalIgnoreCase)
            && !Path.GetExtension(path).Equals(".midi", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MIDI path must end in .mid or .midi.");
        if (requireExisting)
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("MIDI file was not found.", path);
            if (file.Length > 64L * 1024 * 1024) throw new InvalidDataException("MIDI file exceeds the 64 MiB safety limit.");
        }
        else if (!Directory.Exists(Path.GetDirectoryName(path)))
            throw new DirectoryNotFoundException($"MIDI output directory does not exist: {Path.GetDirectoryName(path)}");
        return path;
    }
}
