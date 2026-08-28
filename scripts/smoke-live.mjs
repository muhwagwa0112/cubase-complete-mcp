import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { createHash } from 'node:crypto';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const publishRoot = join(root, 'artifacts', 'publish');
const candidates = [];
for (const entry of readdirSync(publishRoot, { withFileTypes: true })) {
  if (!/^\d{8}-\d{6}$/.test(entry.name)) continue;
  if (!entry.isDirectory()) continue;
  const releaseRoot = join(publishRoot, entry.name);
  const manifestPath = join(releaseRoot, 'release-manifest.json');
  if (!existsSync(manifestPath)) continue;
  try {
    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8').replace(/^\uFEFF/, ''));
    const valid = manifest.schema_version === 1 && manifest.configuration === 'Release'
      && manifest.release_id === entry.name && ['server', 'broker', 'core', 'cli'].every(name => {
        const component = manifest.components?.[name];
        if (!component?.path || !component?.sha256) return false;
        const path = join(releaseRoot, component.path);
        return existsSync(path) && createHash('sha256').update(readFileSync(path)).digest('hex') === component.sha256;
      });
    if (valid) candidates.push({ path: join(releaseRoot, manifest.components.server.path), release: entry.name, manifest });
  } catch { /* Ignore invalid releases. */ }
}
const selected = candidates.sort((a, b) => b.release.localeCompare(a.release))[0];
const server = selected?.path;
if (!server) throw new Error('No published CubaseCompleteMcp.Server.exe was found.');

const child = spawn(server, [], { stdio: ['pipe', 'pipe', 'pipe'] });
let buffer = '';
let nextId = 1;
const pending = new Map();

function send(message) { child.stdin.write(JSON.stringify(message) + '\n'); }
function request(method, params) {
  const id = nextId++;
  send({ jsonrpc: '2.0', id, method, params });
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}
function envelope(response) {
  if (response.error) throw new Error(JSON.stringify(response.error));
  const text = response.result?.content?.[0]?.text;
  if (typeof text !== 'string') throw new Error(`Missing MCP text response: ${JSON.stringify(response)}`);
  return JSON.parse(text);
}
function sleep(milliseconds) { return new Promise(resolve => setTimeout(resolve, milliseconds)); }

child.stderr.pipe(process.stderr);
child.stdout.on('data', chunk => {
  buffer += chunk.toString();
  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    const message = JSON.parse(line);
    const waiter = pending.get(message.id);
    if (waiter) {
      pending.delete(message.id);
      waiter.resolve(message);
    }
  }
});
child.on('error', error => {
  for (const waiter of pending.values()) waiter.reject(error);
  pending.clear();
});

const timeout = setTimeout(() => {
  process.stderr.write('Live MCP smoke test timed out.\n');
  child.kill();
  process.exit(3);
}, 60000);

try {
  await request('initialize', {
    protocolVersion: '2025-06-18', capabilities: {},
    clientInfo: { name: 'cbmcp-live-smoke', version: '1.0' }
  });
  send({ jsonrpc: '2.0', method: 'notifications/initialized' });

  const status = envelope(await request('tools/call', { name: 'cubase_status', arguments: {} }));
  if (!status.data?.driver_online) throw new Error(`CompleteBridge is offline: ${JSON.stringify(status)}`);
  if (status.data?.connection_state !== 'ready') throw new Error(`Connection state is not ready: ${JSON.stringify(status)}`);

  const tracks = envelope(await request('tools/call', {
    name: 'cubase_track', arguments: { action: 'list', options: { max_nodes: 32 } }
  }));
  const mixer = envelope(await request('tools/call', {
    name: 'cubase_mixer', arguments: { action: 'get' }
  }));
  const project = envelope(await request('tools/call', {
    name: 'cubase_project', arguments: { action: 'get' }
  }));
  const plugins = envelope(await request('tools/call', {
    name: 'cubase_plugin', arguments: {
      action: 'list', root: 'selected_track',
      options: { include_parameters: false, max_nodes: 32, max_depth: 3 }
    }
  }));
  const direct = envelope(await request('tools/call', {
    name: 'cubase_direct', arguments: {
      action: 'list', root: 'selected_track',
      options: { include_parameters: true, max_parameters: 16, max_nodes: 8, max_depth: 2 }
    }
  }));
  const ui = envelope(await request('tools/call', {
    name: 'cubase_ui_inspect', arguments: { max_depth: 2, max_elements: 50, include_screenshot: false }
  }));
  for (const [label, response] of Object.entries({ tracks, mixer, project, plugins, direct, ui })) {
    if (response.status !== 'Ok') throw new Error(`${label} read failed: ${JSON.stringify(response)}`);
  }

  const smokeTrackName = `SMK-${Date.now().toString(36).slice(-7)}`;
  const midiDocument = envelope(await request('tools/call', {
    name: 'cubase_midi', arguments: {
      action: 'create_document', name: 'Cubase Live Composition Smoke',
      options: { ppq: 480, tempo_bpm: 124 }
    }
  }));
  const midiDocumentId = midiDocument.data?.summary?.id;
  if (midiDocument.status !== 'Ok' || !midiDocumentId) throw new Error(`MIDI document creation failed: ${JSON.stringify(midiDocument)}`);
  const midiTrack = envelope(await request('tools/call', {
    name: 'cubase_midi', arguments: {
      action: 'add_track', document_id: midiDocumentId, name: smokeTrackName,
      options: { channel: 0 }
    }
  }));
  const midiTrackId = midiTrack.data?.target_id;
  if (midiTrack.status !== 'Ok' || !midiTrackId) throw new Error(`MIDI track creation failed: ${JSON.stringify(midiTrack)}`);
  const midiNotes = envelope(await request('tools/call', {
    name: 'cubase_midi', arguments: { action: 'add_chords', document_id: midiDocumentId, track_id: midiTrackId,
      options: { events: [
          { tick: 0, duration_ticks: 480, notes: [60, 64, 67], velocity: 96 },
          { tick: 480, duration_ticks: 480, notes: [62, 65, 69], velocity: 92 }
        ] }
    }
  }));
  if (midiNotes.status !== 'Ok' || midiNotes.data?.affected_events !== 6) throw new Error(`MIDI chord generation failed: ${JSON.stringify(midiNotes)}`);
  const midiRender = envelope(await request('tools/call', {
    name: 'cubase_midi', arguments: { action: 'render_file', document_id: midiDocumentId }
  }));
  if (midiRender.status !== 'Ok' || midiRender.data?.verified !== true) throw new Error(`MIDI render failed: ${JSON.stringify(midiRender)}`);
  let midiImport = null;
  if (process.env.CBMCP_TEST_MIDI_IMPORT === '1') {
    midiImport = envelope(await request('tools/call', {
      name: 'cubase_midi', arguments: {
        action: 'import_to_cubase', document_id: midiDocumentId,
        options: { accept_import_options: true }
      }
    }));
    if (!['Ok', 'Unverified'].includes(midiImport.status) || midiImport.data?.import_submitted !== true) {
      throw new Error(`MIDI Cubase import failed: ${JSON.stringify(midiImport)}`);
    }
    const observedNames = midiImport.data?.observed_track_names ?? [];
    const strictMethods = ['direct_access_track_names', 'project_ocr_track_names', 'combined_track_names'];
    const postconditionObserved = observedNames.includes(smokeTrackName)
      && strictMethods.includes(midiImport.data?.verification_method);
    if (midiImport.status !== 'Ok' || midiImport.metadata?.verified !== true || !postconditionObserved
        || typeof midiImport.data?.rendered_sha256 !== 'string'
        || !Array.isArray(midiImport.data?.workflow_stages)
        || (midiImport.data?.menu_fallback_count ?? 0) > 1
        || (midiImport.data?.missing_expected_track_names ?? []).length !== 0) {
      throw new Error(`MIDI Cubase track postcondition failed for '${smokeTrackName}': ${JSON.stringify(midiImport)}`);
    }
  }

  // MIDI import can replace or temporarily disable Cubase windows. Never reuse the
  // pre-import snapshot token for the generic UI action contract check.
  const uiForKey = process.env.CBMCP_TEST_MIDI_IMPORT === '1'
    ? envelope(await request('tools/call', {
        name: 'cubase_ui_inspect', arguments: { max_depth: 1, max_elements: 20, include_screenshot: false }
      }))
    : ui;
  if (uiForKey.status !== 'Ok' || !uiForKey.data?.snapshot_id) {
    throw new Error(`Fresh UI snapshot failed: ${JSON.stringify(uiForKey)}`);
  }
  const keyArguments = { action: 'key', snapshot_id: uiForKey.data.snapshot_id, key: 'ESCAPE' };
  const keyProposal = envelope(await request('tools/call', {
    name: 'cubase_ui_invoke', arguments: keyArguments
  }));
  if (keyProposal.status !== 'RequiresConfirmation' || !keyProposal.data?.confirmation_id) {
    throw new Error(`Snapshot key action was not confirmation-gated: ${JSON.stringify(keyProposal)}`);
  }
  const keyCommit = envelope(await request('tools/call', {
    name: 'cubase_ui_invoke',
    arguments: { ...keyArguments, confirmation_id: keyProposal.data.confirmation_id }
  }));
  if (keyCommit.status !== 'Unverified' || keyCommit.data?.invoked !== true) {
    throw new Error(`Snapshot key action failed: ${JSON.stringify(keyCommit)}`);
  }
  const uiAfter = envelope(await request('tools/call', {
    name: 'cubase_ui_inspect', arguments: { max_depth: 1, max_elements: 20, include_screenshot: false }
  }));
  if (uiAfter.status !== 'Ok') throw new Error(`Post-key UI inspection failed: ${JSON.stringify(uiAfter)}`);

  const before = envelope(await request('tools/call', {
    name: 'cubase_transport', arguments: { action: 'get' }
  }));
  const original = before.data?.metronome;
  if (typeof original !== 'boolean') throw new Error(`Metronome state is unavailable: ${JSON.stringify(before)}`);

  const requested = !original;
  let changedReply;
  let changed;
  let restoredReply;
  let restored;
  try {
    for (let setAttempt = 0; setAttempt < 3; setAttempt += 1) {
      changedReply = envelope(await request('tools/call', {
        name: 'cubase_transport', arguments: { action: 'metronome', enabled: requested }
      }));
      for (let observeAttempt = 0; observeAttempt < 10; observeAttempt += 1) {
        await sleep(100);
        changed = envelope(await request('tools/call', {
          name: 'cubase_transport', arguments: { action: 'get' }
        }));
        if (changed.data?.metronome === requested) break;
      }
      if (changed?.data?.metronome === requested) break;
    }
    if (changed?.data?.metronome !== requested) {
      throw new Error(`Metronome postcondition failed: ${JSON.stringify(changed)}`);
    }
  } finally {
    for (let setAttempt = 0; setAttempt < 3; setAttempt += 1) {
      restoredReply = envelope(await request('tools/call', {
        name: 'cubase_transport', arguments: { action: 'metronome', enabled: original }
      }));
      for (let observeAttempt = 0; observeAttempt < 10; observeAttempt += 1) {
        await sleep(100);
        restored = envelope(await request('tools/call', {
          name: 'cubase_transport', arguments: { action: 'get' }
        }));
        if (restored.data?.metronome === original) break;
      }
      if (restored?.data?.metronome === original) break;
    }
    if (restored?.data?.metronome !== original) {
      throw new Error(`Metronome restoration failed: ${JSON.stringify(restored)}`);
    }
  }

  process.stdout.write(JSON.stringify({
    release_id: selected.release,
    git_sha: selected.manifest.git_sha,
    driver_online: status.data.driver_online,
    cubase_version: status.data.cubase_version,
    api13_available: status.data.api13_available,
    track_nodes: tracks.data?.nodes?.length ?? null,
    mixer_read: mixer.status,
    project_read: project.status,
    plugin_nodes: plugins.data?.nodes?.length ?? null,
    direct_nodes: direct.data?.nodes?.length ?? null,
    midi_document: midiDocument.status,
    midi_notes: midiNotes.data?.affected_events,
    midi_rendered_path: midiRender.data?.path,
    midi_import: midiImport?.status ?? 'skipped',
    midi_import_track: midiImport ? smokeTrackName : null,
    midi_import_verification: midiImport?.data?.verification_method ?? null,
    ui_windows: ui.data?.windows?.length ?? null,
    ui_key_confirmation: keyProposal.status,
    ui_key_commit: keyCommit.status,
    ui_post_inspect: uiAfter.status,
    before: original,
    changed: changed.data.metronome,
    restored: restored.data.metronome,
    change_status: changedReply.status,
    restore_status: restoredReply.status,
    final_session_revision: restored.metadata?.session_revision
  }, null, 2) + '\n');
  clearTimeout(timeout);
  child.kill();
  process.exit(0);
} catch (error) {
  clearTimeout(timeout);
  process.stderr.write(`${error.stack || error.message}\n`);
  child.kill();
  process.exit(2);
}
