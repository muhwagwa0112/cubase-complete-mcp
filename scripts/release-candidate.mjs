// Explicit release candidate selection shared by the smoke and live acceptance scripts.
// A candidate is always named on the command line (--release <dir>); the newest timestamp is never picked.
// The manifest (schema 2) must list exactly the files in the directory with matching size, SHA-256 and role.
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, readdirSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

const REQUIRED_ROLES = ['server_managed', 'broker_managed', 'cli_managed', 'core_managed', 'driver_script', 'command_catalog'];

export function argument(name) {
  const index = process.argv.indexOf(name);
  return index >= 0 && index + 1 < process.argv.length ? process.argv[index + 1] : null;
}

function walk(root, directory, files) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (lstatSync(path).isSymbolicLink()) throw new Error(`Release contains a link: ${path}`);
    if (entry.isDirectory()) walk(root, path, files);
    else files.push(relative(root, path).split(sep).join('/'));
  }
  return files;
}

export function sha256File(path) { return createHash('sha256').update(readFileSync(path)).digest('hex'); }

export function selectRelease({ allowDirty = false } = {}) {
  const root = argument('--release');
  if (!root) throw new Error('--release <release-directory> is required; releases are never selected automatically.');
  const manifestPath = join(root, 'release-manifest.json');
  if (!existsSync(manifestPath)) throw new Error('release-manifest.json is missing.');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8').replace(/^﻿/, ''));
  if (manifest.schema_version !== 2) throw new Error('A schema 2 release manifest is required.');
  if (manifest.source_dirty !== false && !allowDirty) throw new Error('The release was built from a dirty tree.');
  if (manifest.pipe_protocol_version !== 2 || manifest.driver_contract_version !== 2) throw new Error('Protocol/driver contract mismatch.');
  const expectedHash = argument('--manifest-sha256');
  const manifestSha256 = sha256File(manifestPath);
  if (expectedHash && expectedHash !== manifestSha256) throw new Error('Manifest hash differs from --manifest-sha256.');
  const listed = new Set();
  for (const file of manifest.files) {
    if (listed.has(file.path.toLowerCase())) throw new Error(`Duplicate path ${file.path}`);
    listed.add(file.path.toLowerCase());
    const path = join(root, ...file.path.split('/'));
    if (!existsSync(path) || lstatSync(path).size !== file.bytes || sha256File(path) !== file.sha256) {
      throw new Error(`Release file does not match the manifest: ${file.path}`);
    }
  }
  const actual = walk(root, root, []).filter(path => path !== 'release-manifest.json');
  const unlisted = actual.filter(path => !listed.has(path.toLowerCase()));
  if (unlisted.length || actual.length !== manifest.files.length) throw new Error(`Release file set differs from the manifest: ${unlisted.slice(0, 5)}`);
  for (const role of REQUIRED_ROLES) if (!manifest.files.some(file => file.role === role)) throw new Error(`Missing role ${role}`);
  const server = join(root, 'CubaseCompleteMcp.Server.exe');
  if (!existsSync(server)) throw new Error('CubaseCompleteMcp.Server.exe is missing from the release.');
  return { root, manifest, manifestSha256, server };
}
