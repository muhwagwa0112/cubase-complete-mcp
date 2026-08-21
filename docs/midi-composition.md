# MIDI composition

`cubase_midi` is the structured composition surface for Cubase Complete MCP. It manages a session-isolated document, renders a standards-compliant MIDI file, and can submit that file through Cubase's `File / Import MIDI File` command. It never edits a `.cpr` file.

## Actions

- Documents: `create_document`, `list_documents`, `get_document`, `delete_document`.
- Tracks and parts: `add_track`, `remove_track`, `rename_track`, `create_part`, `remove_part`.
- Generators: `add_notes`, `add_chords`, `add_drum_pattern`.
- Channel events: `add_cc`, `add_pitch_bend`, `add_channel_pressure`, `add_poly_pressure`, `add_program_change`.
- Maps and text: `add_tempo`, `add_time_signature`, `add_key_signature`, `add_marker`, `add_lyrics`.
- Generic events: `add_events` also accepts `text`, `sysex`, and raw `meta` events.
- Transforms: `quantize`, `transpose`, `humanize`, `replace_range`.
- Files and Cubase: `render_file`, `export_file`, `inspect_file`, `import_file`, `import_to_cubase`.

Document IDs, track IDs and part IDs are opaque. Always use IDs returned by the preceding call. Composition documents are scoped to one MCP broker client session; render a `.mid` file for durable interchange.

## Event schema

All events require `tick`. Channel events accept `channel` 0–15 and inherit the track channel when omitted.

```json
{
  "action": "add_notes",
  "document_id": "midi_...",
  "track_id": "trk_...",
  "part_id": "part_...",
  "events": [
    { "tick": 0, "duration_ticks": 480, "note": 60, "velocity": 100, "release_velocity": 64 },
    { "tick": 480, "duration_ticks": 480, "note": 64, "velocity": 96 }
  ]
}
```

Chord generation accepts an array of pitches per event:

```json
{
  "action": "add_chords",
  "events": [
    { "tick": 0, "duration_ticks": 960, "notes": [60, 64, 67], "velocity": 96 },
    { "tick": 960, "duration_ticks": 960, "notes": [62, 65, 69], "velocity": 92 }
  ]
}
```

Drum generation expands each lane's step indices. MIDI channel 10 is JSON channel `9`:

```json
{
  "action": "add_drum_pattern",
  "events": [
    { "note": 36, "steps": [0, 4, 8, 12], "step_ticks": 120, "velocity": 110 },
    { "note": 38, "steps": [4, 12], "step_ticks": 120, "velocity": 104 }
  ]
}
```

Other event fields:

| Type | Required fields |
|---|---|
| `cc` | `controller` 0–127, `value` 0–127 |
| `pitch_bend` | `bend` -8192–8191 |
| `channel_pressure` | `pressure` 0–127 |
| `poly_pressure` | `note`, `pressure` |
| `program_change` | `program` 0–127 |
| `tempo` | `bpm` 1–1000 |
| `time_signature` | `numerator`, power-of-two `denominator` |
| `key_signature` | `key_sharps_flats` -7–7, optional `minor` |
| `marker`, `lyric`, `text` | `text` |
| `sysex` | `data_hex` without `F0/F7` framing |
| `meta` | `meta_type` 0–127, `data_hex` |

## Transforms

- `quantize`: `grid_ticks`, optional `strength` 0–1, `swing` -1–1 and `quantize_duration`.
- `transpose`: `semitones`; pitches are clamped to 0–127.
- `humanize`: optional `timing_ticks`, `velocity`, and deterministic `seed`.
- `replace_range`: `start_tick`, `end_tick`, and replacement `events`; requires confirmation because existing events are removed.

Every transform can target a whole track or one `part_id`, plus an optional tick range.

## File behavior and limits

- SMF format 0 and 1 with PPQ division are read and written.
- Notes, overlapping notes, running status, channel events, tempo/signature/key meta events, UTF-8 text, raw meta and SysEx are supported.
- Maximums: 256 tracks, 1,000,000 events, 64 MiB input file and MIDI tick `0x0fffffff`.
- Explicit overwrite requires `overwrite=true` and the confirmation flow.
- SMPTE-division MIDI files are rejected because tick transforms require a PPQ grid.
- Cubase-specific VST Note Expression, Expression Maps and Chord Track metadata are not part of Standard MIDI Files; use commands/DirectAccess/UI for those proprietary features.
