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

The stdio process is intentionally thin and safe to start once per MCP client. The broker owns the MIDI ports and Cubase UI boundary. The pipe uses 32-bit big-endian length-prefixed JSON with an 8 MiB limit and `PipeOptions.CurrentUserOnly`.

The MIDI protocol uses manufacturer ID `0x7d` and a `CB2` frame header. Every chunk carries version, kind, session, request, sequence, opcode, chunk coordinates, payload size, and CRC32. Wire bytes are MIDI-safe 7-bit packed. Logical messages are capped at 8 MiB; individual frames at 64 KiB. The broker retransmits unanswered requests up to three times, while the driver caches completed request results so retries are not re-executed.

The driver creates high-level bindings for transport and selected-channel controls, generated command bindings for the full local catalog, and DirectAccess roots for the selected track, MixConsole bank, Control Room, and focused Quick Controls. Runtime ObjectIDs and parameter tags are treated as ephemeral and must be refreshed after an object-change event or project switch.

UI tokens contain no reusable selectors. They expire after 30 seconds, are tied to the current Cubase PID and snapshot, and all sibling tokens from that snapshot are invalidated after one input. `PrintWindow` is the implemented capture path and Windows OCR supplies snapshot-relative words and rectangles. Windows Graphics Capture is not enabled in this version, so custom-rendered controls remain explicitly `best_effort`.

No component writes `.cpr` binaries, injects a DLL, exposes HTTP, changes VST junctions, or accepts windows owned by a process other than Cubase.
