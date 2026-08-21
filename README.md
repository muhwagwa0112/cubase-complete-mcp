# Cubase Complete MCP

`Cubase Complete MCP` is a local, Windows-only MCP server for Cubase 15. It exposes a compact set of MCP tools instead of one tool per Key Command and routes all Cubase ownership through a single per-user broker.

## Current implementation

- Official `ModelContextProtocol` C# SDK 1.4.0 over stdio.
- .NET SDK 8.0.301 pinned by `global.json`; NuGet lock files are committed.
- Current-user-only `\\.\pipe\CubaseCompleteMcp.v1`, singleton broker mutex, concurrent client sessions and five-minute idle shutdown.
- WinMM SysEx v2 transport with 7-bit packing, CRC32, chunks, request/session IDs, nonce handshake, bounded retries, duplicate suppression, stale-session rejection and backpressure limits.
- Generated ES5 MIDI Remote driver with the locally extracted 69 categories / 2,777 Key Commands.
- DirectAccess traversal and parameter writes, v1.3 feature detection for parameter metadata, command `canPerform`, and plugin slot assignment.
- Cubase-PID-scoped UI Automation, single-snapshot element tokens, guarded `SendInput`, and post-action reinspection contract.
- Manifest-backed backup/restore, setup, diagnostics, driver generation and Codex registration CLI.

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
```

## Safe installation order

1. Run `CubaseCompleteMcp.Cli.exe backup pre-setup`.
2. Close Cubase.
3. Run `CubaseCompleteMcp.Cli.exe setup` to install the driver alongside the legacy driver.
4. Start Cubase and open a disposable project so Cubase can instantiate `CBMCP CompleteBridge` on loopMIDI ports `CubaseMCP To Cubase` and `CubaseMCP From Cubase`.
5. Run `CubaseCompleteMcp.Cli.exe doctor` and `node .\scripts\smoke-live.mjs`.
6. Run `CubaseCompleteMcp.Cli.exe register-codex`.
7. Optionally update Cubase to 15.0.20 or later when v1.3-only features are needed. Back up first and rerun the live smoke afterward.
8. Run `setup --deactivate-legacy` only after separately deciding to retire the legacy driver; it is not required for side-by-side use.

`setup` refuses to touch MIDI Remote files while Cubase is running. It creates a new backup and automatically restores it if installation fails. It never changes VST junctions and never edits `.cpr` files.

## MCP tools and resources

Tools: `cubase_status`, `cubase_capability_search`, `cubase_command`, `cubase_transport`, `cubase_track`, `cubase_mixer`, `cubase_plugin`, `cubase_project`, `cubase_ui_inspect`, `cubase_ui_invoke`, `cubase_batch`, and `cubase_diagnostics`.

Resources: `cubase://state`, `cubase://capabilities`, `cubase://commands`, and `cubase://events`.

Destructive actions return `RequiresConfirmation` and a short-lived `confirmation_id`. Re-submit the exact same operation and arguments with that ID to commit it. IDs are single-use and are cryptographically bound to the argument payload.

See [architecture](docs/architecture.md) and the [capability matrix](docs/capability-matrix.md).
