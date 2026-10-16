# Changelog

## 1.0.0 (unreleased)

First public release. Pipe protocol 2, driver contract 2. Validated live on Cubase 15.0.30 (MIDI Remote API 1.3): all
15 live acceptance cases pass and the gate evaluator reports `CODE_RC_READY` 67/67 and `WORKFLOW_RELEASE_READY` 82/82
(candidate `895b582`).

### Features

- 14 MCP tools over a single per-user broker: status, capability search, transport, tracks, mixer, plug-ins,
  DirectAccess, MIDI composition, project, Key Commands, batch, UI inspection/invocation and diagnostics.
- MIDI Remote driver generated from the local Key Commands (every command of the installation is addressable),
  installed by `setup` with a backup and an installation receipt; `doctor` compares the loaded build with it.
- Target handles bound to channel identity, driver epoch and project epoch; approvals bound to targets and files;
  one dispatch per operation with an execution journal and idempotent `operation_id`.
- Observed postconditions: a change is `Ok` only when a fresh host read shows it.
- Plug-ins (API 1.3): installed catalog with class UIDs, slot assignment verified by the loaded UID.
- MIDI composition documents (notes, chords, drums, CC, pitch bend, tempo, markers, ...), SMF import/export, causal
  import into Cubase with tagged tracks, and content comparison with Cubase's own SMF.
- Project open, save, save as with file identities; WAV mixdown (48 kHz/24-bit) of the locator range, verified by its
  header and length.
- Batch with one approval, dependency-aware stopping and a read-only resume plan.

### Safety

- Nothing listens on the network; the broker pipe is current-user only.
- Unclassified Key Commands, deletions, overwrites, recording and plug-in replacement require confirmation.
- A change whose outcome is unknown is never re-sent automatically.
- Cubase-owned dialogs are driven only when they belong to the identity-verified Cubase process; keyboard input is
  sent only while the target window is in the foreground.

### Requirements

Windows 10/11 x64, Cubase 15 (15.0.30 or later for plug-in tools), .NET 8 Desktop Runtime, loopMIDI.
