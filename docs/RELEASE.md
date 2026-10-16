# Releasing

Releases are built by `.github/workflows/release.yml` from a version tag and land as a **draft**; nothing is
published automatically.

1. Make sure `main` is green in CI and the live acceptance of the candidate passed
   ([live acceptance](live-acceptance.md); the evaluator must report `WORKFLOW_RELEASE_READY`).
2. Set `<Version>` in `Directory.Build.props` and replace `(unreleased)` in the matching `CHANGELOG.md` section with
   the release date. Merge that change to `main`.
3. Tag the merge commit and push the tag:

   ```powershell
   git tag -a v1.0.0 -m "Cubase Complete MCP 1.0.0"
   git push origin v1.0.0
   ```

4. The workflow checks that the tag matches `<Version>`, builds, runs the tests, runs `scripts/publish.ps1`, zips the
   release folder as `cubase-complete-mcp-<version>-windows.zip`, writes its `.sha256`, and creates a draft release
   whose notes are the CHANGELOG section plus the release ID, the `release-manifest.json` SHA-256 and the zip SHA-256.
5. Review the draft: download the zip, run `CubaseCompleteMcp.Cli.exe validate-release . --manifest-sha256 <hash>`,
   and install it on a test machine following the [usage guide](USAGE.md). Then publish the draft.

The repository must be public for the release page and the README's "latest release" links to be reachable by
users.
