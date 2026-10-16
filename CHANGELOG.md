# Changelog

## Unreleased

Fixes for defects found while using 1.0.0 live on Cubase 15.0.30. Driver contract 2 is unchanged; run `setup` again
and reload the MIDI Remote script to install the regenerated driver.

### Fixes

- `cubase_project action=export_audio`: the preview file name is no longer compared with spaces removed. OCR of the
  existing name `YuugureDrive_final` read as `YuugureDrive final` matched the requested `YuugureDriveFinal`, so the
  name was not typed, Cubase exported to the old file and the broker timed out waiting for the requested one. The name
  is now typed unless the read equals it position by position up to known OCR confusions (`_`/space, l/I/1, O/0,
  case). After typing, a read that differs only by separators is accepted only when the name shown before was clearly
  another name.
- `cubase_plugin action=list` / `cubase_direct action=list`: one call reads at most `max_total_parameters` (default
  256) parameters and stops after `time_budget_ms` (default 1000 ms), reporting `truncation.budget_exhausted` and a
  `continuation.node_offset`. A 12-channel mixer snapshot with parameters previously ran past the script's time budget
  (`DRIVER_TIMEOUT`) and took the MIDI Remote script offline. `target_handle` (or `bank_index`, `target_scope`) now
  narrows the snapshot to that channel's subtree instead of being ignored, and `object_id` without `parameter_tag`
  reads one object's parameters (paged by `parameter_offset`), for example a plug-in on Stereo Out. Both tools expose
  `max_total_parameters`, `node_offset` and `parameter_offset`; `cubase_plugin` also exposes the snapshot limits.
- `cubase_plugin action=assign`: the driver replies before the host loads the plug-in and runs `trySetSlotPlugin` on
  its next idle callback. Loading Addictive Drums 2 inside the request callback exceeded the script's execution
  timeout, and the assignment came back `OUTCOME_UNKNOWN` although the slot held the requested plug-in. The broker now
  polls the slot for up to 45 s, repeating reads that time out while the plug-in loads (never the assignment), and
  reports a host rejection or load error from the recorded assignment state.
- Transport position and locator writes after a MIDI Remote script reload: a time report Cubase sends while the page
  activates is no longer discarded by the activation reset. When Cubase has not reported the time format yet, the
  `STATE_UNKNOWN` refusal now names the recovery step (Play then Stop once, or a click in the ruler) in its message and
  in `details.recovery`.

## 1.0.0 (2026-10-16)

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
