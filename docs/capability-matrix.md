# Capability matrix

| Area | Primary path | Support | Verification contract |
|---|---|---|---|
| Status, ports, version | Broker / WinMM | Full | Direct process and port observation |
| 69 categories / 2,777 commands | Generated command bindings | Complete classified catalog; execution context-dependent | Exact ID/name plus v1.3 `canPerform`; execution remains `UNVERIFIED` without a state postcondition |
| Play, stop, record, cycle, click, tempo, locators | High-level MIDI Remote bindings | Full after live validation | State callbacks and session revision |
| Track enumeration | Mixer DirectAccess | Dynamic | Fresh object traversal |
| Track create/delete/duplicate/freeze/enable | Key Commands | Command-backed | `canPerform`; destructive delete requires confirmation |
| Volume, pan, mute, solo, monitor, record enable | High-level bindings | Full after live validation | Callback state |
| EQ, filters, sends, channel strip, automation | DirectAccess parameters | Dynamic | Read-back process/display values |
| Plugin parameter enumeration/write | DirectAccess | Generic for exposed parameters | Read-back value, unit and metadata |
| Arbitrary DirectAccess tree/parameter | `cubase_direct` | Dynamic across selected track, mixer, Control Room and focused Quick Controls | Fresh traversal plus process/display/plain-value read-back |
| Plugin slot assignment | API v1.3 plugin manager | Feature-detected | API return plus refreshed slot traversal |
| Control Room / Quick Controls | DirectAccess roots | Dynamic | Fresh traversal/read-back |
| Save, new, open, close, import/export | Key Command then UI | Mixed | File/window/project postcondition required |
| Cubase native dialogs | UI Automation plus guarded snapshot input | Best effort | Reinspect after every action |
| Custom-rendered plugin GUI | OCR coordinates plus guarded click/drag/scroll/key/text | Best effort | Never reported verified without a visible postcondition |
| OCR | Windows.Media.Ocr | Snapshot-scoped | Words and rectangles returned with the screenshot |
| Windows Graphics Capture | None | Not implemented | PrintWindow is used instead |

“Dynamic” means the server supports whatever the current Cubase object tree exposes rather than maintaining a hard-coded plugin-specific schema. `cubase://capabilities` is the machine-readable coverage ledger and reports zero unclassified Key Commands. A third-party plugin can still expose zero automatable parameters, fail to load, or render a control whose state cannot be observed; those cases remain `best_effort` rather than being mislabeled as fully verified.
