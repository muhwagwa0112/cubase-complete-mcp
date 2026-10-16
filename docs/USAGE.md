# Usage guide

How to install Cubase Complete MCP, connect it to Cubase and an MCP client, and use it day to day.

- [Requirements](#requirements)
- [1. Install the release](#1-install-the-release)
- [2. Create the loopMIDI ports](#2-create-the-loopmidi-ports)
- [3. Install the Cubase driver](#3-install-the-cubase-driver)
- [4. Register the MCP server](#4-register-the-mcp-server)
- [5. First steps](#5-first-steps)
- [Working with results](#working-with-results)
- [Tools at a glance](#tools-at-a-glance)
- [WAV export setup](#wav-export-setup)
- [Updating and uninstalling](#updating-and-uninstalling)
- [Troubleshooting](#troubleshooting)

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Cubase | Cubase 15. **15.0.30 or later** (MIDI Remote API 1.3) for the plug-in catalog and plug-in assignment; on 15.0.10 everything else works and plug-in assignment is refused |
| Runtime | [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) |
| MIDI | [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) by Tobias Erichsen |
| MCP client | Codex, Claude Code, Claude Desktop, or any MCP client that runs a local stdio server |

Nothing listens on the network. The server talks to a per-user broker over a named pipe; the broker talks to Cubase
through two loopMIDI ports and Cubase's own windows.

## 1. Install the release

1. Download `cubase-complete-mcp-<version>-windows.zip` from the
   [latest release](https://github.com/muhwagwa0112/cubase-complete-mcp/releases/latest) and note the
   `release-manifest.json` SHA-256 published with it.
2. Extract it to a folder that stays where it is, for example
   `%LOCALAPPDATA%\CubaseCompleteMcp\releases\<release-id>`. MCP clients start the server from this folder.
3. Check the payload before using it:

   ```powershell
   .\CubaseCompleteMcp.Cli.exe validate-release . --manifest-sha256 <sha256-from-the-release-page>
   ```

## 2. Create the loopMIDI ports

Start loopMIDI and add two ports with exactly these names:

| Port | Direction |
|---|---|
| `CubaseMCP To Cubase` | broker → Cubase |
| `CubaseMCP From Cubase` | Cubase → broker |

Leave loopMIDI running (enable "Autostart" in its tray menu). If loopMIDI was started after Cubase, restart Cubase so
MIDI Remote sees the ports. The broker starts an installed loopMIDI itself when the ports are missing.

## 3. Install the Cubase driver

The driver is a MIDI Remote script generated from **your** Key Commands, so every Cubase command you have is
addressable. Close Cubase first.

```powershell
.\CubaseCompleteMcp.Cli.exe backup pre-setup   # optional: snapshot of the current MIDI Remote scripts
.\CubaseCompleteMcp.Cli.exe setup              # generate and install the driver, write an installation receipt
```

Start Cubase and open a project. MIDI Remote detects the driver by its port names; no manual surface setup is needed.
Then check that the loaded driver is the one just installed:

```powershell
.\CubaseCompleteMcp.Cli.exe doctor --release .
```

`setup` refuses to run while Cubase is running, backs up first and restores on failure. Run it again after you change
Key Commands or install a new release.

## 4. Register the MCP server

Use the absolute path of `CubaseCompleteMcp.Server.exe` in the release folder. The server takes no arguments.

**Codex**

```powershell
.\CubaseCompleteMcp.Cli.exe register-codex --release .   # backs up the previous registration first
```

**Claude Code**

```powershell
claude mcp add --scope user cubase-complete -- "C:\Users\<you>\AppData\Local\CubaseCompleteMcp\releases\<release-id>\CubaseCompleteMcp.Server.exe"
```

**Claude Desktop** (`%APPDATA%\Claude\claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "cubase-complete": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\CubaseCompleteMcp\\releases\\<release-id>\\CubaseCompleteMcp.Server.exe"
    }
  }
}
```

Restart the client. The tools appear as `cubase_status`, `cubase_track`, `cubase_transport`, and so on.

## 5. First steps

Ask your agent, or call the tools yourself:

1. **"Check the Cubase connection."** → `cubase_status`. Look for `connection_state: ready`, `driver_online: true`
   and the same `driver_build_id` that `doctor` reported. `api13_available: true` means plug-in tools are usable.
2. **"List the tracks."** → `cubase_track action=list` returns each channel with a `target_handle`. Use these
   handles for every change; they are bound to that channel's identity and expire when the project or driver changes.
3. **"Set the tempo to 96 and turn on the metronome."** → `cubase_transport` (`tempo`, `metronome`).
4. **"Create a MIDI track called Lead and set track B's volume to 0.7."** → `cubase_track action=create`,
   `cubase_mixer action=set target_handle=...`.
5. **"Compose an 8-bar bass line and import it."** → `cubase_midi` (`create_document`, `add_track`, `add_notes`,
   `import_to_cubase`). See [MIDI composition](midi-composition.md).
6. **"Put a StudioEQ on the first insert of track B."** → `cubase_plugin action=catalog target_handle=...` lists the
   installed plug-ins with their class UIDs and the slot it read; `action=assign object_id=<slot> plugin_uid=<uid>`
   loads it and is confirmed only when the slot reports that UID.

Work in a new or backed-up project while you learn the tools. Changing tools never edit `.cpr` files directly.

## Working with results

Every result carries `status` and `execution_state`:

- `Ok`: the change was observed in Cubase (or was already true, `not_applied`).
- `RequiresConfirmation` + `not_started`: nothing changed yet. Review `data.summary`, then send the **identical**
  request again with `confirmation_id`. Deletions, overwrites, recording, plug-in replacement and unclassified Key
  Commands always ask.
- `Unverified` + `unknown`: the change may have reached Cubase. It is **never re-sent automatically**; read the state
  (`cubase_track list`, `cubase_transport get`, ...) before deciding what to do.
- `Error` / `Offline` / `Unsupported`: refused or failed; `error.code` says why.

`operation_id` (a UUID) makes a request idempotent: the same ID with the same plan returns the recorded result, and
`cubase_diagnostics action=operation_status operation_id=...` reads it back later.

## Tools at a glance

| Tool | Use it for |
|---|---|
| `cubase_status` | Connection, driver build, API level, warnings |
| `cubase_capability_search` | Find features and Key Commands with their support level and risk |
| `cubase_transport` | Play, stop, record, cycle, metronome, tempo, position and locators |
| `cubase_track` | List and read tracks; create, rename, duplicate, delete, freeze, enable; move the selection (`next`, `previous`) |
| `cubase_mixer` | Volume, pan, mute, solo, monitor, record enable of a target channel |
| `cubase_plugin` | Plug-in catalog, slot inspection, assignment, plug-in parameters |
| `cubase_direct` | Bounded DirectAccess snapshots and single-parameter writes inside a target |
| `cubase_midi` | MIDI composition documents, SMF import/export, import into Cubase, content comparison |
| `cubase_project` | Open, save, save as, WAV mixdown export |
| `cubase_command` | Any Key Command from your catalog (`can_perform`, `execute`) |
| `cubase_batch` | Several operations with one approval and dependency-aware stopping |
| `cubase_ui_inspect` / `cubase_ui_invoke` | Inspect and drive Cubase-owned UI as a last resort |
| `cubase_diagnostics` | Journal entries, broker and driver diagnostics |

The [capability matrix](capability-matrix.md) lists how each area is verified and how far it has been validated.

## WAV export setup

Cubase's Export Audio Mixdown dialog has no addressable controls and keeps its own settings, so set it once by hand:

1. **File > Export > Audio Mixdown**.
2. File Type **Wave**, Sample Rate **48.0 kHz**, Bit Depth **24 bit**.
3. Path drop-down: **Use Project Mixdown Folder**.
4. Close the dialog.

Then `cubase_project action=export_audio path=<project folder>\Mixdown\<name>.wav expected_duration_seconds=<locator range>`
names the file, reads every setting back from the dialog and exports the locator range once. If a setting differs it
starts nothing and returns `EXPORT_SETTINGS_MISMATCH` with what it read. Keep Cubase in front while it runs; the
dialog takes keyboard input.

## Updating and uninstalling

- **Update**: extract the new release next to the old one, close Cubase, run `setup` from the new folder, restart
  Cubase, and point your MCP client at the new `CubaseCompleteMcp.Server.exe` (`register-codex --release <new>` for
  Codex). A broker of the old release keeps running until it exits; end `CubaseCompleteMcp.Broker.exe` in Task
  Manager if `cubase_status` reports `release_mismatch: true`.
- **Uninstall**: `CubaseCompleteMcp.Cli.exe unregister-codex` (Codex) or remove the server from your client's
  configuration, then delete
  `Documents\Steinberg\Cubase\MIDI Remote\Driver Scripts\Local\CBMCP` with Cubase closed. Backups made by `backup` and
  `setup` stay in `%LOCALAPPDATA%\CubaseCompleteMCP\backups` and can be restored with `restore <dir> --confirm`.

## Troubleshooting

| `cubase_status` shows | Do this |
|---|---|
| `connection_state: loopmidi_missing` | Install loopMIDI and create the two ports ([step 2](#2-create-the-loopmidi-ports)) |
| `connection_state: ports_missing` | The two ports do not exist or are named differently; check the exact names |
| `driver_handshake_timeout` | Open a project in Cubase (the driver activates with a project); if loopMIDI was started after Cubase, restart Cubase |
| `driver_build_id` differs from `doctor` | Close Cubase, run `setup` again, restart Cubase |
| `api13_available: false` | Cubase older than 15.0.30: plug-in catalog and assignment are unavailable; update Cubase |
| `release_mismatch: true` | A broker of another release is running; end `CubaseCompleteMcp.Broker.exe` and call `cubase_status` again |

| Error code | Meaning |
|---|---|
| `TARGET_CHANGED` / `STALE_TARGET` | The selection or channel changed after planning; list the tracks again and retry with the new handle |
| `FOREGROUND_NOT_CUBASE` | A rename or dialog step needs Cubase in front; bring it to the front and retry |
| `FOREIGN_FILE_DIALOG_OPEN` | Another Cubase file dialog is open; close it first |
| `EXPORT_SETTINGS_MISMATCH` | The export dialog is not set as in [WAV export setup](#wav-export-setup) |
| `PLUGIN_CATALOG_UNAVAILABLE` | The host has no API 1.3 plug-in manager |

Logs and state live in `%LOCALAPPDATA%\CubaseCompleteMCP` (journal, snapshots, installation receipts, backups).
