# Cubase Complete MCP

<p align="center">
  <strong>If Cubase Complete MCP saves you time, consider supporting its continued development, testing, and maintenance.</strong>
</p>

<p align="center">
  <a href="https://ko-fi.com/muhwagwa0112">
    <img src="docs/assets/kofi-support-red.png"
         alt="Support Muhwagwa0112 on Ko-fi"
         width="420">
  </a>
</p>

`Cubase Complete MCP` is a local, Windows-only MCP server for Cubase 15. It exposes a compact set of MCP tools instead of one tool per Key Command and routes all Cubase ownership through a single per-user broker.

## Quick start

Requirements: Windows 10/11, Cubase 15 (15.0.30 or later for plug-in tools), the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) and [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html).

1. Download `cubase-complete-mcp-<version>-windows.zip` from the [latest release](https://github.com/muhwagwa0112/cubase-complete-mcp/releases/latest) and extract it to a permanent folder.
2. In loopMIDI, create the ports `CubaseMCP To Cubase` and `CubaseMCP From Cubase`.
3. Close Cubase, run `CubaseCompleteMcp.Cli.exe setup`, start Cubase and open a project.
4. Register `CubaseCompleteMcp.Server.exe` with your MCP client (`CubaseCompleteMcp.Cli.exe register-codex --release .` for Codex, `claude mcp add` for Claude Code, or `claude_desktop_config.json` for Claude Desktop).
5. Ask your agent to "check the Cubase connection" (`cubase_status`) and to "list the tracks".

The [usage guide](docs/USAGE.md) covers every step, the tools, result handling, WAV export setup, updating and troubleshooting.

## Status of this version (pipe protocol 2, driver contract 2)

What is implemented and how far it has been validated is stated per area in the [capability matrix](docs/capability-matrix.md). In short:

- **Offline-validated** (unit, fake-host and generated-driver tests, Windows build): session isolation, no automatic resend of changes, execution journal, target handles, approvals bound to targets/files, mutation serialization, observed postconditions, batch contract, release integrity.
- **Validated on a live Cubase 15.0.30 fixture** (MIDI Remote API 1.3, owned acceptance project, 2026-10-16; all 15 live cases pass and the gate evaluator reports `WORKFLOW_RELEASE_READY` 82/82): driver load and build identity, transport (play/stop/tempo/cycle/metronome/locators, a 2 s recording with no physical input), track list and identities, track create (MIDI, instrument) and rename, target-bound mixer writes (another channel unchanged; pan on instrument channels through their Panner child), the installed plug-in catalog, plug-in slot assignment verified by the loaded class UID, two concurrent clients, project save/save-as/open, causal MIDI import with the part observed in Cubase's own SMF and its content compared with the source, a 48 kHz/24-bit WAV mixdown of the locator range, a driver re-activation that refuses old handles, the selection-change refusal and batch failure/resubmission.
- **Earlier host, Cubase 15.0.10 (API 1.2)**: the plug-in catalog and assignment are unavailable (no plug-in enumeration or UID read-back) and are refused as `PLUGIN_CATALOG_UNAVAILABLE` / `UNSUPPORTED_CAPABILITY`.
- **Operator setup for WAV export**: Export Audio Mixdown keeps its own format settings and has no addressable controls, so it must be set once to Wave / 48 kHz / 24 bit with Path "Use Project Mixdown Folder". The broker types the file name, reads every setting back by OCR, and starts nothing when any of them differs (`EXPORT_SETTINGS_MISMATCH`).

Nothing in this repository claims a live Cubase run that has not happened. The finalization evidence and final report describe exactly which gates passed.

## Result semantics

Every tool result has `status` and `execution_state`:

| status | execution_state | meaning |
|---|---|---|
| `Ok` | `applied` / `not_applied` / `not_applicable` | The declared postcondition was observed by a fresh host read (`not_applied`: already satisfied, nothing sent). |
| `Unverified` | `applied` | The change was observed, but the full postcondition could not be proven. |
| `Unverified` | `unknown` + `OUTCOME_UNKNOWN` | The change may have reached Cubase and its effect is unknown. **It is never re-sent automatically; read state before deciding.** |
| `RequiresConfirmation` | `not_started` | Nothing changed. Commit the identical request with `confirmation_id`. |
| `Error` / `Offline` / `Unsupported` | usually `not_started` | Refused or failed; partial batch results keep `partially_applied`. |

## Migration from protocol 1

- Pipe name `\\.\pipe\CubaseCompleteMcp.v2`, broker mutex `Local\CubaseCompleteMcp.Broker.v2`. A protocol-1 broker is neither reused nor terminated; old clients are refused with `BROKER_PROTOCOL_MISMATCH`.
- The MIDI wire framing stays version 2; the driver now declares `driver_contract_version = 2` and a `driver_build_id` in its hello. Writes are refused (`DRIVER_CONTRACT_MISMATCH`) until the new driver is installed (`setup`) and Cubase reloads it.
- `track_id` on `cubase_track` / `cubase_mixer` (a 0–63 bank slot in protocol 1) is rejected with `LEGACY_TARGET_AMBIGUOUS`. Use `target_handle` from `cubase_track action=list`, `bank_index`, or `target_scope=selected`. `cubase_midi`'s `track_id` is a composition document ID and is unchanged.
- `cubase_transport` positions use `position_text` + `time_format` (the host's primary time display format); the numeric `value` field is gone.
- All changing tools accept `operation_id` (UUID). The server issues one when omitted and returns it; a confirmation commit reuses the ID stored with the approved plan. Re-sending the same ID and plan returns the recorded result without re-executing; a different plan under the same ID is `IDEMPOTENCY_CONFLICT`.
- `cubase_batch` exposes `continue_on_unverified` (default false), `timeout_ms` (≤ 180000), `depends_on` per item and `resume_operation_id` (read-only resume plan). Counts are `requested/evaluated/dispatched/applied/verified/unverified/failed/not_started`; `executed` is a legacy alias of `dispatched_count`.
- Raw Key Commands that no explicit rule classifies now require confirmation (`effective_risk = Confirm`).
- `cubase_diagnostics action=operation_status operation_id=...` reads the journal entry of an operation owned by the same client session.
- Release manifests are schema 2 and list the complete payload; registration and smoke tests take an explicit `--release <dir>`.

## Architecture in one paragraph

stdio MCP server → current-user named pipe (protocol 2) → singleton broker → (a) WinMM SysEx to the generated ES5 MIDI Remote driver, (b) Cubase-owned dialogs through their Win32 controls (file dialogs), verified-foreground keyboard input (Add Track) and OCR of question boxes (project activation), with UI Automation as a fallback. The broker normalizes every request, plans it (targets, file identities, preconditions), gets approval when needed without holding the mutation lease, then re-plans under the lease, journals `dispatching`, dispatches once and observes. See [architecture](docs/architecture.md).

## Build and test

```powershell
dotnet restore CubaseCompleteMcp.sln --locked-mode
dotnet build CubaseCompleteMcp.sln -c Release --no-restore
dotnet test CubaseCompleteMcp.sln -c Release --no-build
```

Node.js is a required test dependency (the generated driver runs in a Node `vm` mock host). Missing Node fails the test run with an explicit reason.

```powershell
# Clean tree only; stages, validates and atomically publishes <output>/<release-id>/
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Output C:\cbmcp-releases
node .\scripts\smoke-mcp.mjs --release C:\cbmcp-releases\<release-id>
CubaseCompleteMcp.Cli.exe register-codex --release C:\cbmcp-releases\<release-id> --validate-only
```

`scripts/live-acceptance.mjs` is the live fixture harness. It refuses to run without an authorization file granting live fixture mutation and a fixture directory it created itself; see [live acceptance](docs/live-acceptance.md).

## Safe installation order

1. `CubaseCompleteMcp.Cli.exe backup pre-setup`
2. Close Cubase.
3. `CubaseCompleteMcp.Cli.exe setup` installs the driver generated from the local Key Commands and writes an installation receipt (catalog input hash, installed driver/catalog hashes, expected build ID).
4. Start Cubase with a disposable project, then `CubaseCompleteMcp.Cli.exe doctor --release <dir>` compares the loaded driver build ID with the receipt.
5. `CubaseCompleteMcp.Cli.exe register-codex --release <dir>` (backs up the previous registration first).

`setup` refuses to run while Cubase is running, backs up first and restores on failure. Nothing edits `.cpr` files, injects DLLs, changes VST junctions or exposes HTTP.

## MCP tools and resources

Tools: `cubase_status`, `cubase_capability_search`, `cubase_command`, `cubase_transport`, `cubase_track`, `cubase_mixer`, `cubase_plugin`, `cubase_direct`, `cubase_midi`, `cubase_project`, `cubase_ui_inspect`, `cubase_ui_invoke`, `cubase_batch`, `cubase_diagnostics`.

Resources: `cubase://state`, `cubase://capabilities`, `cubase://commands`, `cubase://events`.

The Key Command catalog is generated from the local `Key Commands.xml` (69 categories; the count depends on the installation). A catalog entry means a binding exists, not that its effect was verified.

MIDI composition: see [MIDI composition](docs/midi-composition.md), including import verification levels (`submitted`, `targets_observed`, `parts_observed`, `content_roundtrip_verified`).

To remove stale session-scoped composition documents, preview with `cleanup-sessions --older-than-days <N>` and repeat with the returned `--confirmation-id`.

## Support

Questions and bug reports: [issues](https://github.com/muhwagwa0112/cubase-complete-mcp/issues) (use a new empty project and remove private data). Security problems: see [SECURITY.md](SECURITY.md).

If the project is useful to you, you can support it on [Ko-fi](https://ko-fi.com/muhwagwa0112).

## License

[MIT](LICENSE). Cubase and Steinberg are trademarks of Steinberg Media Technologies GmbH; this project is not affiliated with or endorsed by Steinberg. loopMIDI is a product of Tobias Erichsen.
