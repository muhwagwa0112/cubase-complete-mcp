import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readdirSync, statSync } from 'node:fs';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const publishRoot = join(root, 'artifacts', 'publish');
const candidates = [];
for (const entry of readdirSync(publishRoot, { withFileTypes: true })) {
  const candidate = entry.isDirectory() ? join(publishRoot, entry.name, 'CubaseCompleteMcp.Server.exe') : null;
  if (candidate) {
    try { candidates.push({ path: candidate, modified: statSync(candidate).mtimeMs }); } catch { /* incomplete publish */ }
  }
}
const server = candidates.sort((a, b) => b.modified - a.modified)[0]?.path;
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
}, 20000);

try {
  await request('initialize', {
    protocolVersion: '2025-06-18', capabilities: {},
    clientInfo: { name: 'cbmcp-live-smoke', version: '1.0' }
  });
  send({ jsonrpc: '2.0', method: 'notifications/initialized' });

  const status = envelope(await request('tools/call', { name: 'cubase_status', arguments: {} }));
  if (!status.data?.driver_online) throw new Error(`CompleteBridge is offline: ${JSON.stringify(status)}`);

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

  const keyArguments = { action: 'key', snapshot_id: ui.data?.snapshot_id, key: 'ESCAPE' };
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
    changedReply = envelope(await request('tools/call', {
      name: 'cubase_transport', arguments: { action: 'metronome', enabled: requested }
    }));
    for (let attempt = 0; attempt < 10; attempt += 1) {
      await sleep(100);
      changed = envelope(await request('tools/call', {
        name: 'cubase_transport', arguments: { action: 'get' }
      }));
      if (changed.data?.metronome === requested) break;
    }
    if (changed?.data?.metronome !== requested) {
      throw new Error(`Metronome postcondition failed: ${JSON.stringify(changed)}`);
    }
  } finally {
    restoredReply = envelope(await request('tools/call', {
      name: 'cubase_transport', arguments: { action: 'metronome', enabled: original }
    }));
    for (let attempt = 0; attempt < 10; attempt += 1) {
      await sleep(100);
      restored = envelope(await request('tools/call', {
        name: 'cubase_transport', arguments: { action: 'get' }
      }));
      if (restored.data?.metronome === original) break;
    }
    if (restored?.data?.metronome !== original) {
      throw new Error(`Metronome restoration failed: ${JSON.stringify(restored)}`);
    }
  }

  process.stdout.write(JSON.stringify({
    driver_online: status.data.driver_online,
    cubase_version: status.data.cubase_version,
    api13_available: status.data.api13_available,
    track_nodes: tracks.data?.nodes?.length ?? null,
    mixer_read: mixer.status,
    project_read: project.status,
    plugin_nodes: plugins.data?.nodes?.length ?? null,
    direct_nodes: direct.data?.nodes?.length ?? null,
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
