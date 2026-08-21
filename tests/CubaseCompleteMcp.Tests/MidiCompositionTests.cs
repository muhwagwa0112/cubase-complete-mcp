using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class MidiCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-midi-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MidiCompositionService _service;

    public MidiCompositionTests()
    {
        Directory.CreateDirectory(_root);
        _service = new MidiCompositionService(_root);
    }

    [Fact]
    public void CompleteComposition_RendersAndRoundTripsStandardMidi()
    {
        const string session = "test-session";
        var created = _service.Execute("create_document", new JsonObject
        {
            ["name"] = "Complete Song", ["ppq"] = 960, ["tempo_bpm"] = 128.5,
            ["numerator"] = 7, ["denominator"] = 8
        }, session);
        var documentId = created["summary"]!["id"]!.GetValue<string>();
        var track = _service.Execute("add_track", new JsonObject
        {
            ["document_id"] = documentId, ["name"] = "Piano", ["channel"] = 2
        }, session);
        var trackId = track["target_id"]!.GetValue<string>();
        var part = _service.Execute("create_part", new JsonObject
        {
            ["document_id"] = documentId, ["track_id"] = trackId, ["name"] = "Verse",
            ["start_tick"] = 1920, ["length_ticks"] = 7680
        }, session);
        var partId = part["target_id"]!.GetValue<string>();

        _service.Execute("add_notes", WithTarget(documentId, trackId, partId, new JsonArray
        {
            new JsonObject { ["tick"] = 0, ["duration_ticks"] = 960, ["note"] = 60, ["velocity"] = 101 },
            new JsonObject { ["tick"] = 960, ["duration_ticks"] = 480, ["note"] = 62, ["velocity"] = 99 }
        }), session);
        _service.Execute("add_chords", WithTarget(documentId, trackId, partId, new JsonArray
        {
            new JsonObject { ["tick"] = 1920, ["duration_ticks"] = 960, ["notes"] = new JsonArray(60, 64, 67), ["velocity"] = 90 }
        }), session);
        _service.Execute("add_drum_pattern", new JsonObject
        {
            ["document_id"] = documentId, ["track_id"] = trackId,
            ["events"] = new JsonArray
            {
                new JsonObject { ["note"] = 36, ["steps"] = new JsonArray(0, 4, 8, 12), ["step_ticks"] = 240, ["velocity"] = 110 }
            }
        }, session);
        AddEvent(documentId, trackId, "add_cc", new JsonObject { ["tick"] = 0, ["controller"] = 1, ["value"] = 64 }, session);
        AddEvent(documentId, trackId, "add_pitch_bend", new JsonObject { ["tick"] = 120, ["bend"] = -2048 }, session);
        AddEvent(documentId, trackId, "add_channel_pressure", new JsonObject { ["tick"] = 240, ["pressure"] = 77 }, session);
        AddEvent(documentId, trackId, "add_poly_pressure", new JsonObject { ["tick"] = 360, ["note"] = 60, ["pressure"] = 55 }, session);
        AddEvent(documentId, trackId, "add_program_change", new JsonObject { ["tick"] = 0, ["program"] = 12 }, session);
        AddEvent(documentId, trackId, "add_tempo", new JsonObject { ["tick"] = 3840, ["bpm"] = 96.0 }, session);
        AddEvent(documentId, trackId, "add_time_signature", new JsonObject { ["tick"] = 7680, ["numerator"] = 4, ["denominator"] = 4 }, session);
        AddEvent(documentId, trackId, "add_key_signature", new JsonObject { ["tick"] = 0, ["key_sharps_flats"] = -3, ["minor"] = true }, session);
        AddEvent(documentId, trackId, "add_marker", new JsonObject { ["tick"] = 1920, ["text"] = "Verse" }, session);
        AddEvent(documentId, trackId, "add_lyrics", new JsonObject { ["tick"] = 1920, ["text"] = "Hello" }, session);
        AddEvent(documentId, trackId, "add_events", new JsonObject { ["type"] = "sysex", ["tick"] = 0, ["data_hex"] = "7d 01 02" }, session);

        var rendered = _service.Execute("render_file", new JsonObject { ["document_id"] = documentId }, session);
        var path = rendered["path"]!.GetValue<string>();
        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("MThd", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        var decoded = StandardMidiFile.Read(bytes, "Decoded");
        Assert.Equal(960, decoded.Ppq);
        Assert.Equal(2, decoded.Tracks.Count);
        Assert.Contains(decoded.Tracks.SelectMany(item => item.Events), item => item.Type == "note" && item.Note == 60 && item.Tick == 1920);
        Assert.Contains(decoded.Tracks.SelectMany(item => item.Events), item => item.Type == "cc" && item.Controller == 1 && item.Value == 64);
        Assert.Contains(decoded.Tracks.SelectMany(item => item.Events), item => item.Type == "pitch_bend" && item.Bend == -2048);
        Assert.Contains(decoded.Tracks.SelectMany(item => item.Events), item => item.Type == "tempo" && Math.Abs(item.Bpm!.Value - 96) < 0.01);
        Assert.Contains(decoded.Tracks.SelectMany(item => item.Events), item => item.Type == "marker" && item.Text == "Verse");
        Assert.Contains(decoded.Tracks.SelectMany(item => item.Events), item => item.Type == "sysex" && item.DataHex == "7d0102");

        var imported = _service.Execute("import_file", new JsonObject { ["path"] = path, ["name"] = "Imported Copy" }, session);
        Assert.Equal("Imported Copy", imported["document"]!["name"]!.GetValue<string>());
        Assert.True(imported["summary"]!["notes"]!.GetValue<int>() >= 9);
    }

    [Fact]
    public void Transformations_AreBoundedDeterministicAndReplaceIsExact()
    {
        const string session = "transform-session";
        var created = _service.Execute("create", new JsonObject { ["name"] = "Transforms", ["ppq"] = 480 }, session);
        var documentId = created["summary"]!["id"]!.GetValue<string>();
        var trackId = _service.Execute("add_track", new JsonObject
        {
            ["document_id"] = documentId, ["name"] = "Lead", ["channel"] = 0
        }, session)["target_id"]!.GetValue<string>();
        _service.Execute("add_notes", WithTarget(documentId, trackId, null, new JsonArray
        {
            new JsonObject { ["tick"] = 117, ["duration_ticks"] = 111, ["note"] = 125, ["velocity"] = 126 },
            new JsonObject { ["tick"] = 360, ["duration_ticks"] = 120, ["note"] = 2, ["velocity"] = 2 }
        }), session);
        var quantized = _service.Execute("quantize", new JsonObject
        {
            ["document_id"] = documentId, ["track_id"] = trackId, ["grid_ticks"] = 120,
            ["strength"] = 1.0, ["quantize_duration"] = true
        }, session);
        Assert.Equal(1, quantized["affected_events"]!.GetValue<int>());
        _service.Execute("transpose", new JsonObject
        {
            ["document_id"] = documentId, ["track_id"] = trackId, ["semitones"] = 12
        }, session);
        _service.Execute("humanize", new JsonObject
        {
            ["document_id"] = documentId, ["track_id"] = trackId, ["timing_ticks"] = 5,
            ["velocity"] = 8, ["seed"] = 42
        }, session);
        var first = _service.Execute("get", new JsonObject { ["document_id"] = documentId }, session);
        var notes = first["document"]!["tracks"]![1]!["events"]!.AsArray();
        Assert.All(notes, note => Assert.InRange(note!["note"]!.GetValue<int>(), 0, 127));
        Assert.All(notes, note => Assert.InRange(note!["velocity"]!.GetValue<int>(), 1, 127));

        var replaced = _service.Execute("replace_range", new JsonObject
        {
            ["document_id"] = documentId, ["track_id"] = trackId, ["start_tick"] = 0, ["end_tick"] = 1000,
            ["events"] = new JsonArray { new JsonObject { ["type"] = "note", ["tick"] = 240, ["duration_ticks"] = 240, ["note"] = 64, ["velocity"] = 80 } }
        }, session);
        Assert.Equal(2, replaced["removed"]!.GetValue<int>());
        Assert.Equal(1, replaced["affected_events"]!.GetValue<int>());
    }

    [Fact]
    public void Documents_AreSessionIsolated_AndOverwriteRequiresExplicitFlag()
    {
        var created = _service.Execute("create", new JsonObject { ["name"] = "Private" }, "session-a");
        var id = created["summary"]!["id"]!.GetValue<string>();
        Assert.Throws<FileNotFoundException>(() => _service.Execute("get", new JsonObject { ["document_id"] = id }, "session-b"));
        Assert.Equal(0, _service.Execute("list", new JsonObject(), "session-b")["count"]!.GetValue<int>());

        var output = Path.Combine(_root, "output.mid");
        _service.Execute("render", new JsonObject { ["document_id"] = id, ["path"] = output }, "session-a");
        Assert.Throws<IOException>(() => _service.Execute("render", new JsonObject { ["document_id"] = id, ["path"] = output }, "session-a"));
        var overwritten = _service.Execute("render", new JsonObject
        {
            ["document_id"] = id, ["path"] = output, ["overwrite"] = true
        }, "session-a");
        Assert.True(overwritten["verified"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("", "Missing MIDI MThd header")]
    [InlineData("4d546864000000060002000101e0", "format 2")]
    [InlineData("4d5468640000000600010001e728", "SMPTE")]
    public void Reader_RejectsMalformedOrUnsupportedFiles(string hex, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => StandardMidiFile.Read(Convert.FromHexString(hex)));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityRegistry_ListsEveryCompositionAction()
    {
        var actions = CapabilityRegistry.MidiCompositionActions();
        Assert.Equal(actions.Length, actions.Select(item => item.Action).Distinct(StringComparer.Ordinal).Count());
        Assert.True(actions.Length >= 31);
        Assert.Contains(actions, item => item.Action == "import_to_cubase" && item.SupportLevel == "command_and_ui");
        Assert.Contains(actions, item => item.Action == "replace_range" && item.Risk == RiskLevel.Confirm);
    }

    private void AddEvent(string documentId, string trackId, string action, JsonObject item, string session) =>
        _service.Execute(action, new JsonObject
        {
            ["document_id"] = documentId, ["track_id"] = trackId, ["events"] = new JsonArray(item)
        }, session);

    private static JsonObject WithTarget(string documentId, string trackId, string? partId, JsonArray events)
    {
        var value = new JsonObject { ["document_id"] = documentId, ["track_id"] = trackId, ["events"] = events };
        if (partId is not null) value["part_id"] = partId;
        return value;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
