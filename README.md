# Cubase Complete MCP

`Cubase Complete MCP` is a local, Windows-only MCP server for Cubase 15. It exposes a compact set of MCP tools instead of one tool per Key Command and routes all Cubase ownership through a single per-user broker.

## Current implementation

- Official `ModelContextProtocol` C# SDK 1.4.0 over stdio.
- .NET SDK 8.0.301 pinned by `global.json`; NuGet lock files are committed.
- Current-user-only `\\.\pipe\CubaseCompleteMcp.v1`, singleton broker mutex, concurrent client sessions and five-minute idle shutdown.
- WinMM SysEx v2 transport with 7-bit packing, CRC32, chunks, request/session IDs, nonce handshake, bounded retries, duplicate suppression, stale-session rejection and backpressure limits.
- Generated ES5 MIDI Remote driver with the locally extracted 69 categories / 2,777 Key Commands.
- DirectAccess traversal and parameter writes, v1.3 feature detection for parameter metadata, command `canPerform`, and plugin slot assignment.
- Complete capability registry that classifies every extracted command plus all DirectAccess roots and UI input actions.
- Session-isolated structured MIDI composition documents with deterministic Standard MIDI File generation and Cubase import.
- Cubase-PID-scoped UI Automation, OCR-backed snapshot coordinates, guarded click/drag/scroll/key/text `SendInput`, single-use session tokens, and an explicit caller-owned post-action reinspection contract.
- Manifest-backed backup/restore, setup, diagnostics, driver generation and Codex registration CLI.
- Manifest-and-SHA-256-validated timestamped Releases plus a two-step confirmation CLI for stale composition-session cleanup.
- Broker-managed loopMIDI recovery: when the required ports are absent and loopMIDI is not running, the standard installation is product-checked, started once, and polled for up to eight seconds. Set `CUBASE_COMPLETE_MCP_AUTO_START_LOOPMIDI=0` to opt out.
- Pipe protocol and Release identity diagnostics. Compatible mixed Releases continue with an explicit warning; incompatible pipe protocol versions are rejected.

Cubase 15.0.10 is supported through the v1.2 DirectAccess and command-execution baseline. Cubase 15.0.20 or later is required for v1.3 command `canPerform`, extended parameter metadata, and plugin-slot assignment. On older builds those specific paths are feature-detected and reported as unavailable rather than failing driver load. Custom-rendered third-party plugin GUIs remain `best_effort`; the server does not claim that UI as fully supported when neither DirectAccess nor UI Automation exposes it.

## Build and test

```powershell
dotnet restore CubaseCompleteMcp.sln --locked-mode
dotnet build CubaseCompleteMcp.sln -c Release --no-restore
dotnet test CubaseCompleteMcp.sln -c Release --no-build
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
node .\scripts\smoke-mcp.mjs
# With Cubase open on a disposable project:
node .\scripts\smoke-live.mjs
# Include composition rendering and a real File > Import > MIDI File round trip:
$env:CBMCP_TEST_MIDI_IMPORT = '1'; node .\scripts\smoke-live.mjs
```

`publish.ps1` writes `release-manifest.json` with the Git SHA, dirty-worktree flag, SDK version and hashes of the Server, Broker, Core and CLI. `register-codex` ignores flat or manifest-less artifacts; use `--allow-debug` only for an explicit development registration.

## Safe installation order

1. Run `CubaseCompleteMcp.Cli.exe backup pre-setup`.
2. Close Cubase.
3. Run `CubaseCompleteMcp.Cli.exe setup` to install the driver alongside the legacy driver.
4. Start Cubase and open a disposable project. The Broker starts loopMIDI when needed; if Cubase was already running before the ports appeared, `doctor` may report `cubase_restart_required` rather than restarting Cubase automatically.
5. Run `CubaseCompleteMcp.Cli.exe doctor` and `node .\scripts\smoke-live.mjs`.
6. Run `CubaseCompleteMcp.Cli.exe register-codex`.
7. Optionally update Cubase to 15.0.20 or later when v1.3-only features are needed. Back up first and rerun the live smoke afterward.
8. Run `setup --deactivate-legacy` only after separately deciding to retire the legacy driver; it is not required for side-by-side use.

`setup` refuses to touch MIDI Remote files while Cubase is running. It creates a new backup and automatically restores it if installation fails. It never changes VST junctions and never edits `.cpr` files.

## MCP tools and resources

Tools: `cubase_status`, `cubase_capability_search`, `cubase_command`, `cubase_transport`, `cubase_track`, `cubase_mixer`, `cubase_plugin`, `cubase_direct`, `cubase_midi`, `cubase_project`, `cubase_ui_inspect`, `cubase_ui_invoke`, `cubase_batch`, and `cubase_diagnostics`.

Resources: `cubase://state`, `cubase://capabilities`, `cubase://commands`, and `cubase://events`.

`cubase_midi` creates tracks/parts, notes, chords, drum patterns, CC, pitch bend, channel/poly pressure, program changes, tempo/time/key signatures, markers, lyrics, raw meta/SysEx, quantize, transpose, deterministic humanize, range replacement, SMF import/export, and `import_to_cubase`. See [MIDI composition](docs/midi-composition.md) for schemas and examples.

Destructive commands and all arbitrary UI inputs return `RequiresConfirmation` and a short-lived `confirmation_id`. Re-submit the exact same operation and arguments with that ID to commit it. IDs and UI snapshots are session-bound and single-use; a mismatched retry is rejected without burning the approval. MIDI document/track/part deletion, range replacement, and explicit file overwrite use the same confirmation flow. `cubase://capabilities` contains the full command catalog, the four DirectAccess roots, the complete UI action vocabulary, and all MIDI composition actions; `classified_commands` counts the Key Commands that matched an explicit risk rule, and `unclassified_commands` counts the ones that still fall through to the default `Low` risk and need review.

`cubase_ui_invoke` always returns `Unverified` after delivering an input. Its snapshot is consumed, so call `cubase_ui_inspect` again and verify the resulting Cubase state from a fresh snapshot.

`cubase_status` reports loopMIDI installation/process/autostart state, configured and observed ports, client/Broker Release IDs, and a normalized connection state. Track listing remains strictly `Offline` while CompleteBridge is offline; it does not substitute a partial OCR list.

MIDI import executes the command once, waits for each Cubase dialog stage, and permits at most one menu fallback. A file picker is closed only after all matching MIDI dialogs remain absent for three checks. Track names are then polled through DirectAccess and project OCR for up to ten seconds; graph changes alone never verify an import.

To permanently remove only stale session-scoped composition documents and renders, first preview with `cleanup-sessions --older-than-days <N>`, then repeat the same age with the returned `--confirmation-id`. The proposal expires after five minutes and cleanup refuses to run while the broker is active.

See [architecture](docs/architecture.md) and the [capability matrix](docs/capability-matrix.md).
