// Offline acceptance runner for the finalization package (gates G00-G05; live gates are recorded as blocked).
//
//   node scripts/offline-acceptance.mjs --package <finalization-package-dir> --evidence <new-evidence-dir>
//        [--authorization <authorization.json>]
//
// It requires a clean candidate tree, runs locked restore/build/tests/publish through scripts/publish.ps1, parses the
// TRX results, and writes one receipt per case whose mapped tests (tests/acceptance-map.json) all ran and passed.
// It never runs a live Cubase mutation. Cubase is only probed read-only (file version, process presence).
import { spawnSync, spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import os from 'node:os';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
function argument(name) { const index = process.argv.indexOf(name); return index >= 0 ? process.argv[index + 1] : null; }
const packageRoot = resolve(argument('--package') ?? '');
const evidence = resolve(argument('--evidence') ?? '');
if (!existsSync(join(packageRoot, 'acceptance', 'cases.json'))) throw new Error('--package must point to the finalization package');
if (existsSync(evidence)) throw new Error('--evidence must be a new directory');
mkdirSync(join(evidence, 'logs'), { recursive: true });
mkdirSync(join(evidence, 'receipts'), { recursive: true });
mkdirSync(join(evidence, 'traces'), { recursive: true });

const cases = JSON.parse(readFileSync(join(packageRoot, 'acceptance', 'cases.json'), 'utf8')).cases;
const baseline = JSON.parse(readFileSync(join(packageRoot, 'acceptance', 'gates.json'), 'utf8')).baseline_sha;
const map = JSON.parse(readFileSync(join(root, 'tests', 'acceptance-map.json'), 'utf8'));
const runId = 'offline-' + new Date().toISOString().replace(/[:.]/g, '') + '-' + randomBytes(3).toString('hex');
const sha256 = path => createHash('sha256').update(readFileSync(path)).digest('hex');
const rel = path => relative(evidence, path).split(sep).join('/');
function run(command, args, options = {}) {
  const started = new Date().toISOString();
  const result = spawnSync(command, args, { cwd: root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, ...options });
  return { command: [command, ...args].join(' '), started, finished: new Date().toISOString(), status: result.status,
    stdout: result.stdout ?? '', stderr: result.stderr ?? (result.error ? String(result.error) : '') };
}
function writeLog(name, lines) {
  const path = join(evidence, 'logs', name);
  writeFileSync(path, (Array.isArray(lines) ? lines.join('\n') : lines) + '\n');
  return { path: rel(path), sha256: sha256(path), kind: 'log' };
}

// ---- G00: baseline, environment, authorization ---------------------------------------------------------------
const head = run('git', ['rev-parse', 'HEAD']).stdout.trim();
const porcelain = run('git', ['status', '--porcelain=v1']).stdout.trim();
const baselinePresent = run('git', ['cat-file', '-t', baseline]);
const ancestor = run('git', ['merge-base', '--is-ancestor', baseline, 'HEAD']);
const branch = run('git', ['rev-parse', '--abbrev-ref', 'HEAD']).stdout.trim();
const originMain = run('git', ['rev-parse', 'origin/main']).stdout.trim();
const clean = porcelain === '';
if (!clean) throw new Error('The candidate tree is dirty; commit first. Nothing was run.');
const facts = { base01: { baseline, baseline_present: baselinePresent.status === 0, head, branch, origin_main: originMain,
  baseline_is_ancestor: ancestor.status === 0, source_dirty: !clean } };
const baseLog = writeLog('BASE-01.log', [JSON.stringify(facts.base01, null, 2), '$ git log --oneline ' + baseline + '..HEAD',
  run('git', ['log', '--oneline', `${baseline}..HEAD`]).stdout]);

const cubaseExe = 'C:\\Program Files\\Steinberg\\Cubase 15\\Cubase15.exe';
const cubaseVersion = existsSync(cubaseExe)
  ? run('powershell', ['-NoProfile', '-Command', `(Get-Item '${cubaseExe}').VersionInfo.ProductVersion`]).stdout.trim() : null;
const environment = {
  kind: 'environment_record', recorded_at_utc: new Date().toISOString(), baseline_sha: baseline, candidate_sha: head,
  source_dirty: !clean, os: process.platform === 'win32' ? 'Windows' : process.platform, os_version: os.release(), architecture: os.arch(),
  interactive_desktop: null, dotnet_sdk: run('dotnet', ['--version']).stdout.trim() || null, node_version: process.version,
  cubase_version: cubaseVersion, cubase_running: run('tasklist', ['/FI', 'IMAGENAME eq Cubase15.exe']).stdout.includes('Cubase15.exe'),
  loopmidi_running: run('tasklist', ['/FI', 'IMAGENAME eq loopMIDI.exe']).stdout.includes('loopMIDI.exe'),
  pipe_protocol_version: 2, driver_contract_version: 2, driver_build_id: null, expected_driver_build_id: null,
  installation_receipt_sha256: null, fixture_id: null, fixture_owned: false, authorization_reference: null,
  install_or_runtime_probe_performed: false,
  blocked_reasons: ['No authorization for driver installation, Cubase restart or fixture mutation was granted; live gates were not run.']
};
writeFileSync(join(evidence, 'environment.json'), JSON.stringify(environment, null, 2));
const envLog = writeLog('BASE-02.log', JSON.stringify(environment, null, 2));
if (!environment.dotnet_sdk || !environment.node_version) throw new Error('.NET SDK or Node.js missing; environment absence is not converted into a pass.');

// ---- REL-01: locked restore/build/test/publish of the candidate ------------------------------------------------
const releaseOut = join(evidence, 'release');
const publishLogs = join(evidence, 'logs', 'publish');
const publish = run('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', join(root, 'scripts', 'publish.ps1'),
  '-Output', releaseOut, '-LogDirectory', publishLogs]);
writeFileSync(join(evidence, 'logs', 'publish.stdout.log'), publish.stdout + '\n' + publish.stderr);
if (publish.status !== 0) throw new Error('publish.ps1 failed; see logs/publish.stdout.log');
const releaseDir = readdirSync(releaseOut).filter(name => !name.startsWith('.')).map(name => join(releaseOut, name))[0];
const manifestPath = join(releaseDir, 'release-manifest.json');
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
if (manifest.candidate_git_sha !== head || manifest.source_dirty !== false) throw new Error('The published manifest does not describe the clean candidate.');

// ---- TRX -------------------------------------------------------------------------------------------------------
const trxDir = join(publishLogs, 'trx');
const tests = [];
const counters = [];
for (const file of readdirSync(trxDir).filter(name => name.endsWith('.trx'))) {
  const xml = readFileSync(join(trxDir, file), 'utf8');
  for (const match of xml.matchAll(/<UnitTestResult\b[^>]*?testName="([^"]+)"[^>]*?outcome="([^"]+)"[^>]*>/g)) {
    tests.push({ name: match[1].replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>'), outcome: match[2], trx: file });
  }
  const counter = xml.match(/<Counters\b([^>]*)\/>/);
  if (counter) counters.push({ trx: file, attributes: counter[1].trim() });
}
const totalPassed = tests.filter(test => test.outcome === 'Passed').length;
const notPassed = tests.filter(test => test.outcome !== 'Passed');
writeLog('test-inventory.log', [`${tests.length} test results, ${totalPassed} passed`, ...counters.map(item => `${item.trx}: ${item.attributes}`),
  ...tests.map(test => `${test.outcome}\t${test.name}\t${test.trx}`)]);
if (tests.length === 0 || notPassed.length) throw new Error(`Test run is not clean: ${tests.length} results, ${notPassed.length} not passed.`);

// ---- protocol trace against the published server (no Cubase interaction) ---------------------------------------
async function protocolProbe() {
  const server = join(releaseDir, 'CubaseCompleteMcp.Server.exe');
  const child = spawn(server, [], { stdio: ['pipe', 'pipe', 'pipe'] });
  let buffer = '';
  const pending = new Map();
  const transcript = [];
  child.stdout.on('data', chunk => {
    buffer += chunk.toString();
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      if (!line) continue;
      const message = JSON.parse(line);
      transcript.push({ direction: 'server->client', at: new Date().toISOString(), message });
      pending.get(message.id)?.(message);
    }
  });
  let id = 0;
  const request = (method, params) => new Promise((resolveRequest, reject) => {
    const current = ++id;
    const timer = setTimeout(() => reject(new Error(`timeout ${method}`)), 30000);
    pending.set(current, message => { clearTimeout(timer); resolveRequest(message); });
    const message = { jsonrpc: '2.0', id: current, method, params };
    transcript.push({ direction: 'client->server', at: new Date().toISOString(), message });
    child.stdin.write(JSON.stringify(message) + '\n');
  });
  try {
    await request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'cbmcp-offline-acceptance', version: '2.0' } });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    const list = await request('tools/list', {});
    const emptyBatch = await request('tools/call', { name: 'cubase_batch', arguments: { operations: [], continue_on_unverified: true, timeout_ms: 1000 } });
    const legacy = await request('tools/call', { name: 'cubase_mixer', arguments: { action: 'set', track_id: '3', parameter: 'volume', process_value: 0.5 } });
    const overLimit = await request('tools/call', { name: 'cubase_batch', arguments: { operations: [{ operation: 'transport', arguments: { action: 'play' } }], timeout_ms: 999999 } });
    return { transcript, list, emptyBatch, legacy, overLimit };
  } finally { child.kill(); }
}
const probe = await protocolProbe();
const envelope = message => JSON.parse(message.result.content[0].text);
const batchSchema = probe.list.result.tools.find(tool => tool.name === 'cubase_batch')?.inputSchema?.properties ?? {};
const probeFacts = {
  batch_schema_has_options: ['continue_on_unverified', 'timeout_ms', 'operation_id', 'resume_operation_id'].every(name => name in batchSchema),
  empty_batch: envelope(probe.emptyBatch), legacy_track_id: envelope(probe.legacy), over_limit_timeout: envelope(probe.overLimit)
};
const tracePath = join(evidence, 'traces', 'mcp-protocol.jsonl');
writeFileSync(tracePath, probe.transcript.map(item => JSON.stringify(item)).join('\n') + '\n');
const traceRef = { path: rel(tracePath), sha256: sha256(tracePath), kind: 'protocol_trace' };
const protocolOk = probeFacts.batch_schema_has_options
  && probeFacts.empty_batch.error?.code === 'BATCH_INVALID' && probeFacts.empty_batch.execution_state === 'not_started'
  && probeFacts.legacy_track_id.error?.code === 'LEGACY_TARGET_AMBIGUOUS'
  && probeFacts.over_limit_timeout.error?.code === 'BATCH_INVALID';

// ---- BASE-03: live harness refuses without authorization ------------------------------------------------------------
const fixtureParent = join(evidence, 'fixture-probe');
const created = run('node', [join(root, 'scripts', 'live-acceptance.mjs'), 'create-fixture', '--parent', fixtureParent]);
const fixtureRoot = JSON.parse(created.stdout).fixture_root;
const template = join(packageRoot, 'handoff', 'AUTHORIZATION.template.json');
const refusal = run('node', [join(root, 'scripts', 'live-acceptance.mjs'), 'run', '--release', releaseDir, '--manifest-sha256', sha256(manifestPath),
  '--authorization', template, '--fixture', fixtureRoot, '--installation-receipt', template, '--cases', join(packageRoot, 'acceptance', 'cases.json'),
  '--evidence', join(evidence, 'live-refused'), '--candidate-sha', head]);
const base03Log = writeLog('BASE-03.log', ['$ ' + refusal.command, `exit=${refusal.status}`, refusal.stdout, refusal.stderr,
  `live-refused evidence directory created: ${existsSync(join(evidence, 'live-refused', 'receipts'))}`]);
const liveRefused = refusal.status === 4 && refusal.stderr.includes('unfilled template') && !existsSync(join(evidence, 'live-refused', 'receipts'));

// ---- receipts -------------------------------------------------------------------------------------------------------
const results = [];
function receipt(spec, passed, actualFor, command, evidenceRefs, runtime = null, reason = null) {
  const now = new Date().toISOString();
  const body = {
    schema_version: 1, case_id: spec.id, candidate_sha: head, source_dirty: false, run_id: runId, environment_kind: spec.layer,
    outcome: passed ? 'PASS' : 'FAIL', started_at_utc: publish.started, finished_at_utc: now, command_or_action: command,
    exit_code: passed ? 0 : 1, runtime,
    assertions: spec.assertion_ids.map((id, index) => ({ id, expected: spec.expected[index], actual: actualFor(index), passed }))
  };
  const path = join(evidence, 'receipts', `${spec.id}.json`);
  writeFileSync(path, JSON.stringify(body, null, 2));
  results.push({ case_id: spec.id, status: passed ? 'PASS' : 'FAIL', reason, evidence: evidenceRefs,
    receipt: { path: rel(path), sha256: sha256(path), kind: 'receipt' } });
}
function matching(pattern) {
  return tests.filter(test => test.name === pattern || test.name.startsWith(pattern + '(') || test.name.startsWith(pattern + '.'));
}
const testCommand = 'scripts/publish.ps1 (dotnet restore --locked-mode; dotnet build -c Release; dotnet test -c Release --logger trx)';
for (const spec of cases) {
  if (spec.layer === 'live') {
    results.push({ case_id: spec.id, status: 'BLOCKED_AUTH', evidence: [], receipt: null,
      reason: 'No user authorization for driver installation, Cubase restart and owned-fixture live mutations; the live harness was not run.'
        + (spec.id === 'LIVE-04' ? ' Plug-in assignment is additionally BLOCKED_ENV: the installed MIDI Remote API (Cubase 15.0.10) has no plug-in enumeration or UID read-back.' : '') });
    continue;
  }
  if (spec.id === 'BASE-01') {
    const ok = facts.base01.baseline_present && facts.base01.baseline_is_ancestor && clean;
    receipt(spec, ok, index => [`baseline ${baseline} present=${facts.base01.baseline_present}`, `HEAD ${head} descends from baseline=${facts.base01.baseline_is_ancestor} (branch ${branch}, origin/main ${originMain})`, `work tree clean=${clean}; no reset/stash performed`][index],
      'git rev-parse/status/merge-base --is-ancestor', [baseLog]);
    continue;
  }
  if (spec.id === 'BASE-02') {
    receipt(spec, true, index => [`os=${environment.os} ${environment.os_version}, dotnet=${environment.dotnet_sdk}, node=${environment.node_version}, cubase=${environment.cubase_version}`,
      'runner aborts when .NET or Node is missing; live gates recorded as BLOCKED, not PASS'][index], 'environment probes (read-only)', [envLog]);
    continue;
  }
  if (spec.id === 'BASE-03') {
    receipt(spec, liveRefused, index => [`live harness exit=${refusal.status}; no MCP client started, no receipts written`,
      'fixture must be harness-created with marker and the open project must be <fixture>/acceptance.cpr; no project is converted'][index],
      'node scripts/live-acceptance.mjs run with the unfilled authorization template', [base03Log]);
    continue;
  }
  if (spec.id === 'REL-01') {
    const manifestRef = { path: rel(manifestPath), sha256: sha256(manifestPath), kind: 'release_manifest' };
    const log = writeLog('REL-01.log', [publish.stdout, `manifest ${manifestRef.path} sha256 ${manifestRef.sha256}`, `tests: ${tests.length} results, all passed`]);
    receipt(spec, tests.length > 0, index => [`manifest candidate_git_sha=${manifest.candidate_git_sha}`, `${tests.length} tests executed, 0 skipped`, 'restore/build/test/publish exit 0'][index],
      testCommand, [log, manifestRef], { os: 'Windows', candidate_sha: head, dotnet_sdk: environment.dotnet_sdk, node_version: environment.node_version });
    continue;
  }
  const patterns = map.cases[spec.id];
  if (!patterns) { results.push({ case_id: spec.id, status: 'NOT_RUN', reason: 'no mapped automated test', evidence: [], receipt: null }); continue; }
  const matched = patterns.map(pattern => ({ pattern, tests: matching(pattern) }));
  const passed = matched.every(item => item.tests.length > 0 && item.tests.every(test => test.outcome === 'Passed'));
  const lines = matched.flatMap(item => [`pattern ${item.pattern}`, ...item.tests.map(test => `  ${test.outcome}\t${test.name}\t(${test.trx})`)]);
  const refs = [writeLog(`${spec.id}.log`, lines)];
  let ok = passed;
  if (spec.required_evidence_kinds.includes('protocol_trace')) { refs.splice(0, 1, traceRef); ok = passed && protocolOk; writeLog(`${spec.id}.tests.log`, lines); }
  receipt(spec, ok, () => `${matched.reduce((sum, item) => sum + item.tests.length, 0)} mapped test results passed: ${patterns.join(', ')}`
    + (spec.required_evidence_kinds.includes('protocol_trace') ? `; published-server protocol probe ok=${protocolOk}` : ''), testCommand, refs);
}

const resultsDoc = {
  schema_version: 1, baseline_sha: baseline, candidate_sha: head, source_dirty: false, run_id: runId,
  runtime: { os: 'Windows', candidate_sha: head, interactive_desktop: null, fixture_owned: false },
  release_manifest: { path: rel(manifestPath), sha256: sha256(manifestPath), kind: 'release_manifest' },
  test_results: results
};
writeFileSync(join(evidence, 'results.json'), JSON.stringify(resultsDoc, null, 2));
const summary = results.reduce((acc, row) => ({ ...acc, [row.status]: (acc[row.status] ?? 0) + 1 }), {});
process.stdout.write(JSON.stringify({ run_id: runId, candidate_sha: head, release: releaseDir, manifest_sha256: sha256(manifestPath),
  tests: tests.length, protocol_probe_ok: protocolOk, live_harness_refused: liveRefused, summary }, null, 2) + '\n');
