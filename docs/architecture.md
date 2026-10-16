# Architecture

```text
MCP client -- stdio --> CubaseCompleteMcp.Server          (operation_id issuance, tool schemas)
                              |
                 current-user Named Pipe, protocol 2       (CubaseCompleteMcp.v2)
                              |
                    singleton CubaseCompleteMcp.Broker
   OperationPlanner -> ConfirmationStore -> MutationCoordinator -> ExecutionJournal -> dispatch -> PostconditionVerifier
                       |                                       |
               WinMM SysEx (wire v2)                     owned dialogs: Win32 / UIA / OCR
                       |                                  PrintWindow / SendInput
          MIDI Remote ES5 driver (contract 2)                     |
                       +-------------------- Cubase 15 -----------+
```

## Pipe and client

The pipe carries 32-bit big-endian length-prefixed JSON (8 MiB cap, `CurrentUserOnly`). `BrokerClient` tracks each request as `NotSent → Sending → WaitingForResponse → Completed`. Any failure discards the connection (a late reply could otherwise answer the next request) and response IDs must match. A request that may have left is never re-sent: writes return `Unverified / unknown / OUTCOME_UNKNOWN`; session-scoped reads return `CLIENT_SESSION_LOST`; only stateless diagnostics (`status`, `capabilities.*`, `commands.list`, `diagnostics status`) retry once on a new connection. Each pipe connection is a broker client session; confirmations, target handles, compositions and journal entries never move to another session.

## Request pipeline (broker)

1. **Normalize** (`OperationPlanner`): one normalized family/action/arguments value is used for risk, hashing and dispatch. Unknown operations/actions are rejected; protocol-1 `track_id` and conflicting selectors are refused. Unclassified raw commands are `Confirm`.
2. **Plan**: resolve targets (handles, bank slots, pinned selection), file identities (path, size, mtime, SHA-256), slot contents, transport state and other preconditions. The plan hash covers canonical arguments, targets and preconditions (`CanonicalJson`: sorted keys, preserved array order, finite numbers only, control fields excluded).
3. **Approve**: confirm-risk plans return `RequiresConfirmation` with the plan and its `operation_id`. Validation never consumes an approval; consumption is atomic immediately before the first external change. A changed plan returns `TARGET_CHANGED` (or `FILE_CHANGED`) and keeps the approval.
4. **Lease** (`MutationCoordinator`): one mutation workflow per Cubase instance; 5 s wait, 32 queued, `BUSY` otherwise. Re-entrant for the holding flow (a batch holds one lease for all children). Approval waits never hold the lease.
5. **Journal** (`ExecutionJournal`): per-user, owner-bound (hash of the client session), redacted. `dispatching` is flushed before the first external call; a broker crash leaves it as `unknown`, never replayed. Same ID + same plan returns the recorded result; different plan is `IDEMPOTENCY_CONFLICT`.
6. **Dispatch once, observe**: observation repeats reads only, within monotonic deadlines (transport/mixer 3 s, track/plug-in 10 s, MIDI import 30 s total, dialog stage 5 s, project 30 s, export 120 s). A reading only counts when it is newer than the dispatch (per-key state revision in the current driver session) or is a fresh target read.

## Driver (contract 2)

- Hello returns `session_id`, echoed nonce, `driver_contract_version`, `driver_build_id` and feature flags. The broker accepts a hello only with the pending nonce and matching header/body session, then starts a new epoch with an unknown mirror.
- Received frames are filtered (`DriverFrameFilter`): events only from the current session; terminal frames only for a pending request ID + opcode + session + epoch. Discarded frames update nothing.
- The request cache is keyed by session + request ID and payload digest. Stale sessions are refused before the cache; a reused ID with another payload is `REQUEST_ID_CONFLICT`. Retransmission of the same request in the same session is answered from the cache.
- Activation/deactivation resets the mirror to unknown (`null`). Toggles (record, cycle, metronome, mute, …) are refused while their state is unknown and are pulsed at most once toward a target state.
- Targets: `track.list` reports the children of the DirectAccess `VST Mixer` root (up to 256) with their unique ID string and object ID; `bank_index` is only the position among those children. Live on Cubase 15.0.10, calling `HostObject.getUniqueIDString`/`getRuntimeID` from a MIDI callback destabilized Cubase, so identities come from DirectAccess only, and each root is refreshed (`update`) before it is read. The selected track is identified from the `selected_track` root and matched into the mixer children. Writes carry the expected identity and are resolved inside the same host callback; a reordered bank is followed by identity, a replaced object (runtime ID change) is `STALE_TARGET`. Selection-dependent Key Commands carry `expected_selected` and are refused (`TARGET_CHANGED`) if the selection changed; without a guard they are `TARGET_CONTEXT_UNSAFE`.
- Named parameters resolve by exact alias (including Korean titles), never by prefix. Ambiguous, hidden or edit-locked parameters are refused; the driver never steals a lock.
- Writes return receipts (`mutation_sent`, `dispatch_revision`, before/after read-back); the broker decides verification.

## Workflows

Track create/rename, project open/save/save-as, WAV export, plug-in assignment and MIDI import run as staged workflows: one command dispatch, owned dialogs only (window ownership policy: verified Cubase PID + start time, owner chain, stage title **and** control signature, bound to an opaque dialog ID), observed postconditions, and a recorded stage log. Failures report the last stage; workflows never delete files, close projects, repeat Undo or re-dispatch. The only cancel is of a dialog the workflow itself bound.

What Cubase 15.0.10 exposes, as found live: Windows file dialogs are found by process-owned `#32770` windows with a file-name field (1001/1148) and IDOK. The Open and MIDI import dialogs accept `WM_SETTEXT` + IDOK; the Save As dialog ignores `WM_SETTEXT`, so the path is typed only while the dialog is foreground and the file-name field has focus, and read back before IDOK. Cubase's own Add Track dialog exposes no controls: the name is typed while it is the verified foreground window. Cubase question boxes ("Cubase Pro") expose no controls either: on open, the box is read by OCR and only the project-activation question is answered; any other question is left to the user (`pending_user_dialog`). The Export Audio Mixdown dialog exposes no controls, so WAV export fails explicitly. Cubase does not reopen the file of the already active project.

MIDI import renders a temporary copy whose importable tracks are named `<name> [CBMCP-xxxxxxxx]` (derived from the operation ID), records the channel identities that existed before, and accepts only new identities with the tagged titles. MIDI Remote cannot read parts or events, so the automated result stops at `targets_observed` (reported `Unverified` with `IMPORT_CAUSALITY_UNPROVEN`); `compare_file` on a Cubase-exported MIDI file provides `content_roundtrip_verified`.

## Releases

`publish.ps1` refuses dirty trees (unless marked development), runs locked restore/build/tests (failing on skipped or zero tests), publishes into a staging directory, writes manifest v2 (every file with bytes/SHA-256/role; reference-catalog driver with a deterministic build ID = SHA-256(source SHA, catalog input SHA, contract version)), validates it and moves it atomically. `setup` writes an installation receipt for the driver generated from the local catalog. The manifest is an integrity list, not a signature.

No component writes `.cpr` binaries, injects a DLL, exposes HTTP, changes VST junctions, or accepts windows owned by a process other than Cubase.
