# Live acceptance (gates G06–G08)

Live gates prove the workflows on a real Cubase with an **owned fixture**. They are never replaced by mock results. Without explicit authorization and a matching environment the corresponding cases stay `BLOCKED_AUTH` / `BLOCKED_ENV`, and the release cannot be called `WORKFLOW_RELEASE_READY`.

## Preconditions

1. A clean candidate release (`publish.ps1`) and its manifest SHA-256.
2. The driver for that candidate installed with `CubaseCompleteMcp.Cli.exe setup` (Cubase closed), producing an installation receipt, then Cubase restarted so the new driver is loaded.
3. A fixture created by the harness: `node scripts/live-acceptance.mjs create-fixture` (new directory with a random ID and a provenance marker). An existing song is never a fixture, whatever its name.
4. The operator creates a **new empty** Cubase project and saves it as `<fixture>/acceptance.cpr`. The harness never saves, closes or retitles a user project to create one.
5. Cubase 15.0.30 or later (MIDI Remote API 1.3) for the plug-in cases. Export Audio Mixdown set once to Wave / 48 kHz / 24 bit with Path "Use Project Mixdown Folder" (the dialog keeps these; the broker reads them back and refuses any other setting).
6. No broker of another release running: the broker is a singleton that outlives a Cubase restart, and the harness refuses to start when `cubase_status` reports another `broker_release_id`. Stop the old `CubaseCompleteMcp.Broker.exe` first.
7. Cubase stays in the foreground while the harness runs (rename, export and file dialogs take keyboard input).
8. An authorization file written from the user's explicit approval (template: the finalization package's `handoff/AUTHORIZATION.template.json`) with `authorization_reference`, `expires_at_utc`, `fixture_id`, `fixture_root`, and at least `scope.owned_fixture_live_mutations=true`. Recording needs `record_fixture_without_physical_inputs`; the reconnect fault test needs `driver_reconnect_fault_test`. Editing the template is not an approval.

## Run

```powershell
node scripts/live-acceptance.mjs run --release <release-dir> --manifest-sha256 <hash> `
  --authorization <authorization.json> --fixture <fixture-dir> --installation-receipt <receipt.json> `
  --cases <package>/acceptance/cases.json --evidence <new-evidence-dir> --candidate-sha <sha> `
  --join-results <offline-evidence-dir>/results.json
```

The harness refuses to start if any authorization, marker, project identity, release or driver build check fails. It writes one receipt per case (`receipts/<ID>.json`), a JSONL trace per case (`traces/<ID>.jsonl`), hashed copies of the files a case produced or relied on (`artifacts/<ID>-<name>`), and `live-results.json`.

| Case | Raw evidence besides the trace |
|---|---|
| LIVE-01 | `installation_receipt`: the `--installation-receipt` file |
| LIVE-05 | `file_artifact`: the rendered MIDI file that was imported (hash checked against the broker's `rendered_sha256`) |
| LIVE-06 | `file_artifact`: the saved `acceptance-saved.cpr` |
| LIVE-07 | `file_artifact`: `mixdown.wav` |
| E2E-02 | `file_artifact`: the operator's `roundtrip.mid` |
| E2E-03 | `file_artifact`: `mixdown.wav` and `acceptance-saved.cpr` as they are at the end of the run |

An artifact is never overwritten. A file that is missing when its case needs it fails the case.

### One run for the gate evaluator

`evaluate_gates.py` checks every receipt against the single `run_id` of `results.json`. `--join-results` makes the live receipts carry the run_id of the offline run, which must be for the same candidate and the same release manifest (the release the live run installed) and from a clean tree. Then merge both into a new directory:

```powershell
node scripts/merge-acceptance.mjs --base <offline-evidence-dir> --live <live-evidence-dir> --out <new-dir>
python <package>/tools/evaluate_gates.py --results <new-dir>/results.json --evidence-root <new-dir> --profile workflow_release
```

The merge replaces the offline rows of the live cases with the live rows and takes the live runtime facts. It refuses, without writing anything, when the live run did not join the offline run, the candidate or release differ, a referenced file is missing or changed, a path exists in both runs with different content, or the output directory exists. A live run without `--join-results` cannot be merged; its receipts are never rewritten, so the live run is repeated with `--join-results`.

## What is automated and what is not

| Case | Automated | Needs |
|---|---|---|
| LIVE-01 | identity, driver build vs receipt, fixture/project match | — |
| LIVE-02 | target-handle mixer write on B, A unchanged, restore | fixture tracks from LIVE-04 |
| LIVE-03 | metronome toggle/restore | record part needs recording authorization and a fixture without physical inputs |
| LIVE-04 | MIDI + instrument track create, rename, catalog of the instrument channel's first insert slot, assignment of a bundled Steinberg effect, UID read-back | API 1.3 (Cubase 15.0.30) |
| LIVE-05 | same-named decoy track, tagged import, `targets_observed`, part on the tagged track in "Write MIDI File to Project Folder" | — |
| LIVE-06 | save_as into the fixture, save_as to a second file, reopen the first, track presence | — |
| LIVE-07 | locators (moved through a distinct time first), WAV export into `<fixture>/Mixdown`, header/length | host primary time format Bars+Beats; export dialog set to Wave / 48 kHz / 24 bit |
| FAULT-01/02/04 | two clients, selection change before commit, failing batch resubmission | — |
| FAULT-03 | project switch re-activates the driver; old handle refused, new epoch with the installed build, nothing replayed | `driver_reconnect_fault_test` authorization |
| E2E-02 | `compare_file` of the source with Cubase's SMF from LIVE-05 | — |
| E2E-03 | WAV from LIVE-07, saved project, the part compared again after the reopen | — |

Nothing is deleted after the run; the fixture directory and its files are kept for review.
