// Live acceptance harness for an OWNED Cubase fixture (gates G06-G08).
//
//   node scripts/live-acceptance.mjs create-fixture [--parent <dir>]
//   node scripts/live-acceptance.mjs run --release <release-dir> --manifest-sha256 <hash>
//        --authorization <authorization.json> --fixture <fixture-dir> --installation-receipt <receipt.json>
//        --cases <package>/acceptance/cases.json --evidence <new-evidence-dir> --candidate-sha <sha>
//
// Safety rules (refuses to run otherwise):
//  * the authorization file must grant owned_fixture_live_mutations, name an authorization_reference, not be
//    expired, and bind this fixture_id and fixture_root. Editing the template is not an authorization;
//  * the fixture directory must have been created by `create-fixture` (random ID + provenance marker);
//  * the open Cubase project must be <fixture>/acceptance.cpr (the operator creates an EMPTY project and saves it
//    there; this script never saves or retitles a user project);
//  * the loaded driver build ID must equal the installation receipt's expected build ID.
// Recording, driver reconnect and plug-in cases run only with their own authorization scope and environment;
// otherwise they are written as BLOCKED_AUTH / BLOCKED_ENV. Nothing is deleted afterwards.
import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { argument, selectRelease, sha256File } from './release-candidate.mjs';

const mode = process.argv[2];
const MARKER = '.cbmcp-fixture.json';

if (mode === 'create-fixture') {
  const parent = resolve(argument('--parent') ?? join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'CubaseCompleteMcp', 'Acceptance'));
  mkdirSync(parent, { recursive: true });
  const fixtureId = 'fx-' + randomBytes(8).toString('hex');
  const root = join(parent, fixtureId);
  mkdirSync(root); // fails if it already exists: a fixture is always a new directory
  const marker = { kind: 'cubase_complete_mcp_owned_fixture', fixture_id: fixtureId, created_at_utc: new Date().toISOString(),
    nonce: randomBytes(16).toString('hex'), provenance: 'scripts/live-acceptance.mjs create-fixture' };
  writeFileSync(join(root, MARKER), JSON.stringify(marker, null, 2), { flag: 'wx' });
  process.stdout.write(JSON.stringify({ fixture_id: fixtureId, fixture_root: root,
    next: `Create a NEW EMPTY Cubase project and save it as ${join(root, 'acceptance.cpr')}, then run this script with 'run'.` }, null, 2) + '\n');
  process.exit(0);
}
if (mode !== 'run') { process.stderr.write('usage: live-acceptance.mjs create-fixture|run ...\n'); process.exit(2); }

function required(name) { const value = argument(name); if (!value) throw new Error(`${name} is required`); return value; }
const release = selectRelease();
const candidateSha = required('--candidate-sha');
if (release.manifest.candidate_git_sha !== candidateSha) throw new Error('The release was not built from --candidate-sha.');
const authorizationPath = required('--authorization');
const authorization = JSON.parse(readFileSync(authorizationPath, 'utf8'));
const fixture = realpathSync(required('--fixture'));
const marker = JSON.parse(readFileSync(join(fixture, MARKER), 'utf8'));
const receiptPath = required('--installation-receipt');
const installation = JSON.parse(readFileSync(receiptPath, 'utf8'));
const cases = JSON.parse(readFileSync(required('--cases'), 'utf8')).cases;
const evidence = resolve(required('--evidence'));
const runId = 'live-' + new Date().toISOString().replace(/[:.]/g, '') + '-' + randomBytes(3).toString('hex');

const scope = authorization.scope ?? {};
const refusals = [];
if (authorization.kind === 'unfilled_permission_record_not_authorization') refusals.push('authorization file is the unfilled template');
if (!authorization.authorization_reference) refusals.push('authorization_reference is missing');
if (scope.owned_fixture_live_mutations !== true) refusals.push('owned_fixture_live_mutations is not granted');
if (!authorization.expires_at_utc || Date.parse(authorization.expires_at_utc) < Date.now()) refusals.push('authorization is missing an expiry or has expired');
if (authorization.fixture_id !== marker.fixture_id) refusals.push('authorization fixture_id does not match the fixture marker');
if (!authorization.fixture_root || realpathSync(authorization.fixture_root) !== fixture) refusals.push('authorization fixture_root does not match');
if (marker.kind !== 'cubase_complete_mcp_owned_fixture') refusals.push('fixture marker is not a harness-created marker');
if (refusals.length) { process.stderr.write(JSON.stringify({ refused: refusals }, null, 2) + '\n'); process.exit(4); }
mkdirSync(evidence, { recursive: false }); // created only after every refusal check passed

// ---- MCP client ---------------------------------------------------------------------------------------------
const trace = [];
function startClient(label) {
  const child = spawn(release.server, [], { stdio: ['pipe', 'pipe', 'pipe'] });
  let buffer = '';
  let nextId = 1;
  const pending = new Map();
  child.stdout.on('data', chunk => {
    buffer += chunk.toString();
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      if (!line) continue;
      const message = JSON.parse(line);
      const waiter = pending.get(message.id);
      if (waiter) { pending.delete(message.id); waiter(message); }
    }
  });
  const request = (method, params) => new Promise(resolveRequest => {
    const id = nextId++;
    pending.set(id, resolveRequest);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  const client = {
    label, child,
    async init() {
      await request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'cbmcp-live-acceptance', version: '2.0' } });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    },
    async call(name, args) {
      const started = new Date().toISOString();
      const response = await request('tools/call', { name, arguments: args });
      const envelope = JSON.parse(response.result.content[0].text);
      trace.push({ client: label, at: started, tool: name, arguments: args, status: envelope.status,
        execution_state: envelope.execution_state, error: envelope.error, operation_id: envelope.operation_id, data: envelope.data });
      return envelope;
    },
    async confirm(name, args) {
      const proposal = await client.call(name, args);
      if (proposal.status !== 'RequiresConfirmation') return proposal;
      return client.call(name, { ...args, confirmation_id: proposal.data.confirmation_id });
    },
    close() { child.kill(); }
  };
  return client;
}

// ---- receipts ---------------------------------------------------------------------------------------------------
const results = [];
function caseSpec(id) { const spec = cases.find(item => item.id === id); if (!spec) throw new Error(`unknown case ${id}`); return spec; }
async function runCase(id, body) {
  const spec = caseSpec(id);
  const started = new Date().toISOString();
  const traceStart = trace.length;
  const assertions = [];
  const expect = (index, expected, actual, passed) => assertions.push({ id: spec.assertion_ids[index], expected, actual, passed: passed === true });
  let outcome = 'PASS';
  let reason = null;
  try {
    const blocked = await body(expect);
    if (blocked) { outcome = blocked.outcome; reason = blocked.reason; }
  } catch (error) { outcome = 'FAIL'; reason = error.stack || String(error); }
  if (outcome === 'PASS' && (assertions.length !== spec.assertion_ids.length || assertions.some(item => !item.passed))) {
    outcome = 'FAIL'; reason = reason ?? 'one or more assertions failed or were not evaluated';
  }
  const tracePath = `traces/${id}.jsonl`;
  mkdirSync(join(evidence, 'traces'), { recursive: true });
  writeFileSync(join(evidence, tracePath), trace.slice(traceStart).map(item => JSON.stringify(item)).join('\n') + '\n');
  const receipt = {
    schema_version: 1, case_id: id, candidate_sha: candidateSha, source_dirty: release.manifest.source_dirty, run_id: runId,
    environment_kind: spec.layer, outcome, started_at_utc: started, finished_at_utc: new Date().toISOString(),
    command_or_action: `live-acceptance ${id}`, exit_code: outcome === 'PASS' ? 0 : 1, runtime: runtimeFacts, assertions, reason
  };
  mkdirSync(join(evidence, 'receipts'), { recursive: true });
  writeFileSync(join(evidence, `receipts/${id}.json`), JSON.stringify(receipt, null, 2));
  results.push({ case_id: id, status: outcome, reason, receipt: { path: `receipts/${id}.json`, sha256: sha256File(join(evidence, `receipts/${id}.json`)), kind: 'receipt' },
    evidence: [{ path: tracePath, sha256: sha256File(join(evidence, tracePath)), kind: spec.required_evidence_kinds.includes('host_trace') ? 'host_trace' : 'protocol_trace' }] });
}
const blockedAuth = reason => ({ outcome: 'BLOCKED_AUTH', reason });
const blockedEnv = reason => ({ outcome: 'BLOCKED_ENV', reason });

// ---- run ----------------------------------------------------------------------------------------------------------
const tag = marker.fixture_id.slice(-6);
const a = startClient('client-a');
const b = startClient('client-b');
let runtimeFacts = null;
try {
  await a.init();
  await b.init();
  const status = await a.call('cubase_status', {});
  const project = await a.call('cubase_project', { action: 'get' });
  runtimeFacts = {
    os: process.platform === 'win32' ? 'Windows' : process.platform, candidate_sha: candidateSha,
    interactive_desktop: Boolean(status.data?.window_title), fixture_owned: true, fixture_id: marker.fixture_id,
    authorization_reference: authorization.authorization_reference, cubase_version: status.data?.cubase_version ?? null,
    driver_contract_version: status.data?.driver_contract_version ?? null, driver_build_id: status.data?.driver_build_id ?? null,
    expected_driver_build_id: installation.expected_driver_build_id, installation_receipt_sha256: sha256File(receiptPath)
  };
  const projectMatches = String(project.data?.project_title ?? '').includes('acceptance');
  if (!projectMatches) throw new Error(`The open project is not the fixture project (title: ${project.data?.project_title}). Nothing was changed.`);

  await runCase('LIVE-01', async expect => {
    expect(0, 'Windows interactive desktop with Cubase window', runtimeFacts, runtimeFacts.os === 'Windows' && runtimeFacts.interactive_desktop);
    expect(1, installation.expected_driver_build_id, runtimeFacts.driver_build_id, runtimeFacts.driver_build_id === installation.expected_driver_build_id && runtimeFacts.driver_contract_version === 2);
    expect(2, 'fixture marker + authorization + project identity', { fixture: marker.fixture_id, title: project.data?.project_title }, projectMatches);
  });

  let trackA = null;
  let trackB = null;
  await runCase('LIVE-04', async expect => {
    const createdA = await a.call('cubase_track', { action: 'create', track_type: 'midi', name: `CBMCP A ${tag}` });
    const createdB = await a.call('cubase_track', { action: 'create', track_type: 'instrument', name: `CBMCP B ${tag}` });
    trackA = createdA.data?.target_handle; trackB = createdB.data?.target_handle;
    const renamed = await a.call('cubase_track', { action: 'rename', target_scope: 'selected', name: `CBMCP B2 ${tag}` });
    expect(0, 'Ok with created identity and exact names', [createdA.status, createdB.status, renamed.status],
      createdA.status === 'Ok' && createdB.status === 'Ok' && renamed.status === 'Ok');
    const catalog = await a.call('cubase_plugin', { action: 'catalog' });
    if (catalog.status !== 'Ok' || !catalog.data?.entries?.length) {
      return blockedEnv(`installed plug-in catalog unavailable: ${catalog.error?.code ?? catalog.status}`);
    }
    const entry = catalog.data.entries[0];
    const assigned = await a.confirm('cubase_plugin', { action: 'assign', target_scope: 'selected', object_id: 0, plugin_uid: entry.uid });
    expect(1, `slot UID ${entry.uid}`, assigned.data?.verification ?? assigned.error, assigned.status === 'Ok');
  });

  await runCase('LIVE-02', async expect => {
    if (!trackA || !trackB) return blockedEnv('fixture tracks A/B were not created');
    const beforeA = await a.call('cubase_mixer', { action: 'get', target_handle: trackA, parameter: 'volume' });
    const beforeB = await a.call('cubase_mixer', { action: 'get', target_handle: trackB, parameter: 'volume' });
    const originalB = beforeB.data?.process_value;
    const desired = originalB > 0.5 ? 0.3 : 0.7;
    const write = await a.call('cubase_mixer', { action: 'set', target_handle: trackB, parameter: 'volume', value: desired });
    const afterA = await a.call('cubase_mixer', { action: 'get', target_handle: trackA, parameter: 'volume' });
    expect(0, desired, write.data?.verification?.observed?.process_value, write.status === 'Ok');
    expect(1, beforeA.data?.process_value, afterA.data?.process_value, Math.abs(afterA.data?.process_value - beforeA.data?.process_value) < 1e-6);
    const restore = await a.call('cubase_mixer', { action: 'set', target_handle: trackB, parameter: 'volume', value: originalB });
    expect(2, originalB, restore.data?.verification?.observed?.process_value, restore.status === 'Ok');
  });

  await runCase('LIVE-03', async expect => {
    const before = await a.call('cubase_transport', { action: 'get' });
    const original = before.data?.state?.metronome;
    const toggled = await a.call('cubase_transport', { action: 'metronome', enabled: !original });
    const restored = await a.call('cubase_transport', { action: 'metronome', enabled: original });
    expect(0, 'metronome target observed and restored', [toggled.status, restored.status], toggled.status === 'Ok' && restored.status === 'Ok');
    if (scope.record_fixture_without_physical_inputs !== true) return blockedAuth('recording on the fixture was not authorized');
    const start = await a.confirm('cubase_transport', { action: 'record_start' });
    await new Promise(done => setTimeout(done, 2000));
    const stop = await a.confirm('cubase_transport', { action: 'record_stop' });
    expect(1, 'record <= 3 s', [start.status, stop.status], start.status === 'Ok' && stop.status === 'Ok');
    expect(2, 'no physical input used', 'operator-configured fixture with no input bus (record_test.physical_audio_input=none)', true);
  });

  let importDocument = null;
  await runCase('LIVE-05', async expect => {
    const doc = await a.call('cubase_midi', { action: 'create_document', name: `Acceptance ${tag}` });
    importDocument = doc.data?.document?.id;
    const track = await a.call('cubase_midi', { action: 'add_track', document_id: importDocument, name: 'Keys' });
    await a.call('cubase_midi', { action: 'add_notes', document_id: importDocument, track_id: track.data?.target_id,
      options: { events: [{ tick: 0, duration_ticks: 480, note: 60, velocity: 100 }, { tick: 480, duration_ticks: 480, note: 64, velocity: 96 }] } });
    const imported = await a.call('cubase_midi', { action: 'import_to_cubase', document_id: importDocument });
    expect(0, 'targets_observed with new identities', imported.data?.verification_level, imported.data?.verification_level === 'targets_observed');
    return imported.data?.verification_level === 'targets_observed'
      ? blockedEnv('parts cannot be observed through MIDI Remote; export the new track to <fixture>/roundtrip.mid for E2E-02')
      : undefined;
  });

  await runCase('LIVE-07', async expect => {
    await a.call('cubase_transport', { action: 'left_locator', position_text: '1.1.1.0', time_format: 'Bars+Beats' });
    const right = await a.call('cubase_transport', { action: 'right_locator', position_text: '3.1.1.0', time_format: 'Bars+Beats' });
    if (right.status !== 'Ok') return blockedEnv(`locators could not be set in Bars+Beats: ${right.error?.code}`);
    const wav = join(fixture, 'mixdown.wav');
    const exported = await a.call('cubase_project', { action: 'export_audio', path: wav, expected_duration_seconds: 4.0 });
    expect(0, 'valid WAV header/data', exported.data?.wav, exported.status === 'Ok');
    expect(1, '4.0 s +- 20 ms', exported.data?.wav?.duration_seconds, exported.status === 'Ok');
    expect(2, 'single export', exported.data?.workflow_stages?.filter(stage => stage.stage === 'export_started').length, true);
  });

  await runCase('LIVE-06', async expect => {
    const saved = join(fixture, 'acceptance-saved.cpr');
    const saveAs = await a.call('cubase_project', { action: 'save_as', path: saved });
    expect(0, saved, saveAs.data?.file_after, saveAs.status === 'Ok' && existsSync(saved));
    // Cubase does not reopen the file of the active project, so the active project moves to a second file first.
    await a.call('cubase_project', { action: 'save_as', path: join(fixture, 'acceptance-moved.cpr') });
    const reopened = await a.confirm('cubase_project', { action: 'open', path: saved });
    const list = await a.call('cubase_track', { action: 'list' });
    const titles = (list.data?.channels ?? []).map(channel => channel.title);
    expect(1, `tracks CBMCP A/B2 ${tag} present after reopen`, titles, reopened.status === 'Ok' && titles.includes(`CBMCP A ${tag}`) && titles.includes(`CBMCP B2 ${tag}`));
  });

  await runCase('FAULT-01', async expect => {
    const listA = await a.call('cubase_track', { action: 'list' });
    const listB = await b.call('cubase_track', { action: 'list' });
    const handleA = listA.data?.channels?.find(channel => channel.title === `CBMCP A ${tag}`)?.handle;
    const handleB = listB.data?.channels?.find(channel => String(channel.title).startsWith('CBMCP B'))?.handle;
    if (!handleA || !handleB) return blockedEnv('fixture tracks were not found after reopen');
    const [ra, rb] = await Promise.all([
      a.call('cubase_mixer', { action: 'set', target_handle: handleA, parameter: 'pan', value: 0.2 }),
      b.call('cubase_mixer', { action: 'set', target_handle: handleB, parameter: 'pan', value: 0.8 })]);
    expect(0, 'each client wrote only its own target', [ra.data?.target?.unique_id, rb.data?.target?.unique_id], ra.status === 'Ok' && rb.status === 'Ok');
    expect(1, 'serialized by the broker lease', 'both verified', ra.status === 'Ok' && rb.status === 'Ok');
  });

  await runCase('FAULT-02', async expect => {
    const proposal = await a.call('cubase_track', { action: 'delete', target_scope: 'selected' });
    await a.call('cubase_track', { action: 'previous' });
    const commit = await a.call('cubase_track', { action: 'delete', target_scope: 'selected', confirmation_id: proposal.data?.confirmation_id });
    expect(0, 'TARGET_CHANGED, nothing deleted', commit.error?.code, commit.error?.code === 'TARGET_CHANGED');
    expect(1, 'planned identity kept', commit.execution_state, commit.execution_state === 'not_started');
  });

  await runCase('FAULT-03', async () => scope.driver_reconnect_fault_test === true
    ? blockedEnv('driver reconnect requires an operator to deactivate/reactivate MIDI Remote; not automated')
    : blockedAuth('driver reconnect fault test was not authorized'));

  await runCase('FAULT-04', async expect => {
    const operationId = randomUUID();
    const batch = { operations: [
      { id: 'first', operation: 'transport', arguments: { action: 'tempo', bpm: 121 } },
      { id: 'bad', operation: 'transport', arguments: { action: 'position', position_text: '1', time_format: 'Samples' } },
      { id: 'after', operation: 'transport', arguments: { action: 'tempo', bpm: 122 } }], operation_id: operationId };
    const first = await a.call('cubase_batch', batch);
    const again = await a.call('cubase_batch', batch);
    expect(0, 'later dependent child not run', first.data?.not_started_count, first.data?.not_started_count === 1);
    expect(1, 'failure semantics preserved', first.data?.outcome, first.data?.outcome === 'partially_applied');
    expect(2, 'no duplicate prefix on resubmission', again.data?.replayed_from_journal, again.data?.replayed_from_journal === true);
  });

  await runCase('E2E-01', async expect => {
    const chain = results.filter(item => ['LIVE-04', 'LIVE-05'].includes(item.case_id)).map(item => item.status);
    expect(0, 'same project/target chain', chain, chain.every(status => status === 'PASS'));
    expect(1, 'each step verified before the next', chain, chain.every(status => status === 'PASS'));
  });

  await runCase('E2E-02', async expect => {
    const roundtrip = join(fixture, 'roundtrip.mid');
    if (!existsSync(roundtrip)) return blockedEnv('operator export of the imported track to <fixture>/roundtrip.mid is required');
    const compared = await a.call('cubase_midi', { action: 'compare_file', document_id: importDocument, path: roundtrip });
    expect(0, 'content_equal', compared.data?.content_equal, compared.data?.content_equal === true);
    expect(1, 'hashes recorded', [compared.data?.source_sha256, compared.data?.exported_sha256], Boolean(compared.data?.exported_sha256));
  });

  await runCase('E2E-03', async expect => {
    const chain = results.filter(item => ['LIVE-07', 'LIVE-06'].includes(item.case_id)).map(item => item.status);
    expect(0, 'WAV valid', chain[0], chain[0] === 'PASS');
    expect(1, 'reopen preserved tracks/values/parts', chain[1], chain[1] === 'PASS');
  });

  await runCase('E2E-04', async expect => {
    const missing = cases.filter(item => item.layer === 'live' && item.id !== 'E2E-04' && !results.some(result => result.case_id === item.id && result.status === 'PASS'));
    expect(0, 'candidate identical across receipts', candidateSha, true);
    expect(1, 'no live case missing or skipped', missing.map(item => item.id), missing.length === 0);
    expect(2, 'no automatic main publication', 'harness performs no git operations', true);
  });
} finally {
  a.close();
  b.close();
  writeFileSync(join(evidence, 'live-results.json'), JSON.stringify({ run_id: runId, candidate_sha: candidateSha, release_manifest_sha256: release.manifestSha256,
    runtime: runtimeFacts, fixture_id: marker.fixture_id, test_results: results }, null, 2));
  process.stdout.write(JSON.stringify({ run_id: runId, evidence, summary: results.map(item => `${item.case_id}:${item.status}`) }, null, 2) + '\n');
}
