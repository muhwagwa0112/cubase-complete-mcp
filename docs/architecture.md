# Architecture

```text
MCP client -- stdio --> CubaseCompleteMcp.Server
                              |
                       current-user Named Pipe
                              |
                    singleton CubaseCompleteMcp.Broker
                       |                         |
               WinMM SysEx v2            UI Automation
                       |                  PrintWindow/SendInput
               MIDI Remote ES5                  |
                       +----------- Cubase 15 --+
```

The broker also owns session-scoped composition documents and a Standard MIDI File codec. Generated files flow through Cubase's command binding and its process-owned file dialog; this path is separate from the SysEx control channel.

The stdio process is intentionally thin and safe to start once per MCP client. The broker owns the MIDI ports and Cubase UI boundary. The pipe uses 32-bit big-endian length-prefixed JSON with an 8 MiB limit and `PipeOptions.CurrentUserOnly`.

The MIDI protocol uses manufacturer ID `0x7d` and a `CB2` frame header. Every chunk carries version, kind, session, request, sequence, opcode, chunk coordinates, payload size, and CRC32. Wire bytes are MIDI-safe 7-bit packed. Logical messages are capped at 8 MiB; individual frames at 64 KiB. The broker retransmits unanswered requests up to three times, while the driver caches completed request results so retries are not re-executed.

The driver creates high-level bindings for transport and selected-channel controls, generated command bindings for the full local catalog, and DirectAccess roots for the selected track, MixConsole bank, Control Room, and focused Quick Controls. Runtime ObjectIDs and parameter tags are treated as ephemeral and must be refreshed after an object-change event or project switch.

The composition layer stores validated JSON documents below the current-user local state root in a directory derived from the broker client-session hash. Documents contain tracks, named parts and normalized MIDI/meta events. The deterministic SMF codec writes PPQ format 0/1 files and decodes them for round-trip inspection. Cubase import renders to an owned path, executes the generated `File / Import MIDI File` binding, and submits only a Cubase-process-owned file dialog; it does not edit `.cpr` data.

UI element and coordinate snapshots contain no reusable selectors. They expire after 30 seconds, are tied to the current Cubase PID and MCP client session, and all sibling tokens from that snapshot are invalidated after one input. `PrintWindow` is the implemented capture path and Windows OCR supplies snapshot-relative words and rectangles. Guarded input supports UIA patterns plus snapshot-relative click, double/right-click, drag, wheel, key chords and Unicode text. Windows Graphics Capture is not enabled in this version, so custom-rendered controls whose state is invisible to `PrintWindow` remain explicitly `best_effort`.

No component writes `.cpr` binaries, injects a DLL, exposes HTTP, changes VST junctions, or accepts windows owned by a process other than Cubase.
