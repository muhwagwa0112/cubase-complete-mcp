# Security policy

## Supported versions

Only the latest published release is eligible for security fixes. The project targets Cubase 15 on Windows 10/11
(validated on Cubase 15.0.30 with MIDI Remote API 1.3).

## Reporting a vulnerability

Use GitHub's private vulnerability reporting for this repository. Do not open a public issue for suspected pipe,
broker, driver, installer, file-path or arbitrary-command vulnerabilities. Include the affected release ID,
reproduction steps, impact, and whether a project or local file was exposed. Maintainers should acknowledge a
complete report within seven days.

Never attach real songs, audio, `%LOCALAPPDATA%\CubaseCompleteMCP` state, Key Command files with private names, or
MCP client configuration that contains secrets. Use a new empty Cubase project as the fixture.

## Local trust boundary

- The MCP server talks to the broker over a named pipe that only the current Windows user can open
  (`\\.\pipe\CubaseCompleteMcp.v2`). The broker reaches Cubase only through two local loopMIDI ports and Cubase's
  own windows; nothing listens on the network and no HTTP endpoint exists.
- Changing operations are planned, approved when their risk requires it, and dispatched once. The pipe ACL and the
  per-user broker mutex keep other Windows users out; they do not sandbox a malicious process that already runs as
  the same user. Treat every process in that session as inside the trust boundary.
- The MIDI Remote driver is generated from the local Key Commands and installed only by `setup`, which backs up the
  previous driver first and writes an installation receipt. `doctor` compares the loaded driver build ID with it.
- Nothing edits `.cpr` files, injects DLLs, changes VST folders or plug-in junctions, or deletes user files.

## Release trust

Each release folder contains `release-manifest.json` (schema 2) listing every payload file with its size, SHA-256 and
role. `CubaseCompleteMcp.Cli.exe validate-release <dir> --manifest-sha256 <hash>` checks it before installation;
compare the hash with the one published on the release page.
