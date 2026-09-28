# Capability matrix

Support levels: **implemented** (code exists), **mock_validated** (offline tests with fake host adapters and the generated driver in a mock MIDI Remote host), **live_validated** (verified on a real Cubase fixture — none yet in this version), **unsupported**, **blocked_environment** (needs an API or environment that is not available), **best_effort**.

| Area | Path | Level | Postcondition / contract |
|---|---|---|---|
| Status, ports, driver identity | Broker / WinMM / hello | implemented, mock_validated | contract version, build ID, epoch, feature flags |
| Key Command catalog (69 categories; count per installation) | Generated command bindings | implemented | A binding exists; execution is `Unverified` (no declared postcondition). Unclassified commands require confirmation. Selection-dependent commands need a target guard. |
| Play / stop / record / cycle / metronome | Transport bindings + callbacks | mock_validated | Fresh callback newer than the dispatch; toggles refused while state unknown; record needs confirmation |
| Tempo | Time display | mock_validated | Fresh BPM within 0.01 |
| Position / locators | Time display | mock_validated | `position_text` in the host's reported `time_format`; other formats `UNSUPPORTED_TIME_FORMAT` |
| Track list | 64-slot MixConsole bank | mock_validated | Handles bind unique/runtime ID; `bank_truncated` when all 64 slots are occupied; channels outside the bank are not listed |
| Volume / pan / mute / solo / monitor / record enable | DirectAccess on the target channel (selected-track binding only for a pinned selected track outside the bank) | mock_validated | Fresh read of the same target (1e-4); ambiguous/hidden/locked refused |
| EQ, sends, strip, other channel parameters | DirectAccess `parameter_tag` within the target | mock_validated | Fresh read of the same object/tag |
| Track delete / duplicate | Guarded Key Command | mock_validated | Identity guard in the driver callback; delete confirmed; observed disappearance / one new identity |
| Track freeze / enable | Guarded Key Command | implemented | No host read-back: `Unverified` |
| Track create (MIDI, instrument, audio) | Key Command + owned Add Track dialog | implemented (fake-UI validated) | One new identity with the requested name |
| Track rename | Guarded Key Command + typed text | implemented (fake-UI validated) | Title of the planned identity |
| Project open / save / save_as | Key Command + owned file dialog | implemented (fake-UI validated) | Title change / file change; overwrite bound to file identity; `FILE_EXISTS` / `FILE_CHANGED` |
| WAV mixdown (PCM, stereo, 48 kHz, 24-bit, locator range) | Key Command + owned export dialog | implemented (fake-UI validated) | RIFF/fmt/data header and length within max(20 ms, host latency); never re-exported |
| Plug-in catalog (installed UIDs) | — | blocked_environment | The installed MIDI Remote API declares no plug-in enumeration; `PLUGIN_CATALOG_UNAVAILABLE` |
| Plug-in slot assignment | API 1.3 `trySetSlotPlugin` | blocked_environment | Requires a catalog UID and UID read-back; title changes are never proof |
| Plug-in parameters | DirectAccess | mock_validated | Fresh read |
| MIDI composition documents, SMF import/export | `cubase_midi` | implemented (existing tests) | Validation and SMF round trip |
| MIDI import into Cubase | SMF + Key Command + owned dialogs | implemented (fake-UI validated) | `targets_observed` at best automatically (reported `Unverified`); `content_roundtrip_verified` via `compare_file` |
| Batch | Broker | mock_validated | Planned children, one approval, dependency-aware stop, journaled prefix, read-only resume plan |
| Cubase dialogs via `cubase_ui_*` | UI Automation / guarded input | best_effort | Always `Unverified`; re-inspect |
| Custom-rendered plug-in GUIs | OCR + guarded input | best_effort | Never reported verified |
| Windows Graphics Capture | — | unsupported | PrintWindow is used |
| Existing .cpr note editing, Note Expression, Expression Maps | — | unsupported | Out of scope |

MIDI Remote API 1.3 features (`canPerform`, extended parameter metadata, plug-in manager) are feature-detected; Cubase 15.0.10 (API 1.2) reports them unavailable without failing driver load. API 1.2 compatibility and API 1.3 full-workflow validation are separate profiles.
