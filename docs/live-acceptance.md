# Live acceptance (gates G06–G08)

Live gates prove the workflows on a real Cubase with an **owned fixture**. They are never replaced by mock results. Without explicit authorization and a matching environment the corresponding cases stay `BLOCKED_AUTH` / `BLOCKED_ENV`, and the release cannot be called `WORKFLOW_RELEASE_READY`.

## Preconditions

1. A clean candidate release (`publish.ps1`) and its manifest SHA-256.
2. The driver for that candidate installed with `CubaseCompleteMcp.Cli.exe setup` (Cubase closed), producing an installation receipt, then Cubase restarted so the new driver is loaded.
3. A fixture created by the harness: `node scripts/live-acceptance.mjs create-fixture` (new directory with a random ID and a provenance marker). An existing song is never a fixture, whatever its name.
4. The operator creates a **new empty** Cubase project and saves it as `<fixture>/acceptance.cpr`. The harness never saves, closes or retitles a user project to create one.
5. An authorization file written from the user's explicit approval (template: the finalization package's `handoff/AUTHORIZATION.template.json`) with `authorization_reference`, `expires_at_utc`, `fixture_id`, `fixture_root`, and at least `scope.owned_fixture_live_mutations=true`. Recording needs `record_fixture_without_physical_inputs`; the reconnect fault test needs `driver_reconnect_fault_test`. Editing the template is not an approval.

## Run

```powershell
node scripts/live-acceptance.mjs run --release <release-dir> --manifest-sha256 <hash> `
  --authorization <authorization.json> --fixture <fixture-dir> --installation-receipt <receipt.json> `
  --cases <package>/acceptance/cases.json --evidence <new-evidence-dir> --candidate-sha <sha>
```

The harness refuses to start if any authorization, marker, project identity, release or driver build check fails. It writes one receipt per case (`receipts/<ID>.json`), a JSONL trace per case (`traces/<ID>.jsonl`) and `live-results.json`, which merge into the package results for `evaluate_gates.py --profile workflow_release`.

## What is automated and what is not

| Case | Automated | Needs |
|---|---|---|
| LIVE-01 | identity, driver build vs receipt, fixture/project match | — |
| LIVE-02 | target-handle mixer write on B, A unchanged, restore | fixture tracks from LIVE-04 |
| LIVE-03 | metronome toggle/restore | record part needs recording authorization and a fixture without physical inputs |
| LIVE-04 | MIDI + instrument track create, rename | plug-in part: `BLOCKED_ENV` until an installed plug-in UID can be enumerated and read back |
| LIVE-05 | tagged import, `targets_observed` | parts are not observable through MIDI Remote (`BLOCKED_ENV` for the part assertion) |
| LIVE-06 | save_as into the fixture, reopen, track presence | — |
| LIVE-07 | locators, WAV export, header/length | host primary time format Bars+Beats |
| FAULT-01/02/04 | two clients, selection change before commit, failing batch resubmission | — |
| FAULT-03 | — | operator-driven MIDI Remote reactivation with authorization |
| E2E-02 | `compare_file` | operator exports the imported track to `<fixture>/roundtrip.mid` |

Nothing is deleted after the run; the fixture directory and its files are kept for review.
