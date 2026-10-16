// Scenario checks for scripts/acceptance-evidence.mjs (live run joining, artifact evidence, offline+live merge).
// Prints one JSON summary line { checks, failures, groups } and exits non-zero on any failure.
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const { attachArtifact, joinedRunId, mergeAcceptance } = await import(pathToFileURL(join(here, '..', '..', '..', 'scripts', 'acceptance-evidence.mjs')).href);
const { sha256File } = await import(pathToFileURL(join(here, '..', '..', '..', 'scripts', 'release-candidate.mjs')).href);

let checks = 0;
const failures = [];
const groups = {};
let current = null;
function group(name) { current = name; groups[name] = 0; }
function check(name, condition, detail) {
  checks++; groups[current]++;
  if (!condition) failures.push(`${current}: ${name}${detail === undefined ? '' : ' -> ' + JSON.stringify(detail)}`);
}
function throws(name, action, pattern) {
  try { action(); check(name, false, 'did not throw'); } catch (error) { check(name, pattern.test(error.message), error.message); }
}

const root = mkdtempSync(join(tmpdir(), 'cbmcp-acceptance-'));
const CANDIDATE = 'a'.repeat(40);
const MANIFEST = 'b'.repeat(64);
const write = (path, text) => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, text); return path; };
const ref = (dir, path, kind) => ({ path, sha256: sha256File(join(dir, path)), kind });

function offlineRun(dir, runId) {
  write(join(dir, 'receipts/OFF-01.json'), JSON.stringify({ case_id: 'OFF-01', run_id: runId }));
  write(join(dir, 'logs/OFF-01.log'), 'offline ok');
  write(join(dir, 'release/r/release-manifest.json'), '{}');
  const results = { schema_version: 1, candidate_sha: CANDIDATE, source_dirty: false, run_id: runId,
    runtime: { os: 'Windows', interactive_desktop: null }, release_manifest: { path: 'release/r/release-manifest.json', sha256: MANIFEST, kind: 'release_manifest' },
    test_results: [
      { case_id: 'OFF-01', status: 'PASS', reason: null, receipt: ref(dir, 'receipts/OFF-01.json', 'receipt'), evidence: [ref(dir, 'logs/OFF-01.log', 'log')] },
      { case_id: 'LIVE-01', status: 'BLOCKED_AUTH', reason: 'not run', receipt: null, evidence: [] }] };
  write(join(dir, 'results.json'), JSON.stringify(results));
  return results;
}

function liveRun(dir, runId, { manifest = MANIFEST, caseId = 'LIVE-01' } = {}) {
  write(join(dir, `receipts/${caseId}.json`), JSON.stringify({ case_id: caseId, run_id: runId }));
  write(join(dir, `traces/${caseId}.jsonl`), '{"tool":"cubase_status"}\n');
  write(join(dir, `artifacts/${caseId}-installation-receipt.json`), '{"expected_driver_build_id":"x"}');
  const results = { run_id: runId, candidate_sha: CANDIDATE, release_manifest_sha256: manifest, fixture_id: 'fx-test',
    runtime: { os: 'Windows', interactive_desktop: true, fixture_owned: true },
    test_results: [{ case_id: caseId, status: 'PASS', reason: null, receipt: ref(dir, `receipts/${caseId}.json`, 'receipt'),
      evidence: [ref(dir, `traces/${caseId}.jsonl`, 'host_trace'), ref(dir, `artifacts/${caseId}-installation-receipt.json`, 'installation_receipt')] }] };
  write(join(dir, 'live-results.json'), JSON.stringify(results));
  return results;
}

try {
  group('joining the offline run');
  const base = { run_id: 'offline-1', candidate_sha: CANDIDATE, source_dirty: false, release_manifest: { sha256: MANIFEST } };
  check('a matching offline run lends its run_id', joinedRunId(base, { candidateSha: CANDIDATE, manifestSha256: MANIFEST }) === 'offline-1');
  throws('another candidate is refused', () => joinedRunId(base, { candidateSha: 'c'.repeat(40), manifestSha256: MANIFEST }), /another candidate/);
  throws('another release is refused', () => joinedRunId(base, { candidateSha: CANDIDATE, manifestSha256: 'd'.repeat(64) }), /another release manifest/);
  throws('a dirty offline run is refused', () => joinedRunId({ ...base, source_dirty: true }, { candidateSha: CANDIDATE, manifestSha256: MANIFEST }), /dirty/);
  throws('an offline run without run_id is refused', () => joinedRunId({ ...base, run_id: '' }, { candidateSha: CANDIDATE, manifestSha256: MANIFEST }), /no run_id/);

  group('artifact evidence');
  const evidence = join(root, 'artifact-evidence');
  const source = write(join(root, 'fixture', 'saved.cpr'), 'project bytes');
  const attached = attachArtifact(evidence, source, 'LIVE-06-saved.cpr');
  check('the copy is referenced under artifacts/ with its kind', attached.path === 'artifacts/LIVE-06-saved.cpr' && attached.kind === 'file_artifact', attached);
  check('the reference hash is the copy hash', attached.sha256 === sha256File(join(evidence, attached.path)) && attached.sha256 === sha256File(source));
  const receiptRef = attachArtifact(evidence, source, 'LIVE-01-installation-receipt.json', 'installation_receipt');
  check('the evidence kind is kept', receiptRef.kind === 'installation_receipt');
  throws('existing evidence is never overwritten', () => attachArtifact(evidence, source, 'LIVE-06-saved.cpr'), /never overwritten/);
  throws('a host-reported hash must match', () => attachArtifact(evidence, source, 'LIVE-05-import.mid', 'file_artifact', 'e'.repeat(64)), /differs from the reported/);
  check('a mismatched artifact is still kept for review', existsSync(join(evidence, 'artifacts/LIVE-05-import.mid')));
  throws('a missing file fails the case', () => attachArtifact(evidence, join(root, 'nope.wav'), 'LIVE-07-mixdown.wav'), /not found/);
  throws('a name cannot leave the evidence directory', () => attachArtifact(evidence, source, '../../escape.cpr'), /escapes/);

  group('merging offline and live runs');
  const offline = join(root, 'offline');
  const offlineResults = offlineRun(offline, 'offline-1');
  write(join(offline, 'gates-code_rc.json'), '{"decision":"CODE_RC_READY"}');
  const offlineHash = sha256File(join(offline, 'results.json'));
  const live = join(root, 'live');
  liveRun(live, 'offline-1');
  const liveHash = sha256File(join(live, 'live-results.json'));
  const out = join(root, 'merged');
  const summary = mergeAcceptance({ base: offline, live, out });
  const merged = JSON.parse(readFileSync(join(out, 'results.json'), 'utf8'));
  check('the run_id stays the offline run_id', merged.run_id === 'offline-1' && summary.run_id === 'offline-1', summary);
  check('the live row replaces the offline row', merged.test_results.find(row => row.case_id === 'LIVE-01').status === 'PASS');
  check('offline rows are kept', merged.test_results.find(row => row.case_id === 'OFF-01').status === 'PASS' && merged.test_results.length === 2);
  check('the live runtime becomes the result runtime', merged.runtime.fixture_owned === true && merged.runtime.interactive_desktop === true);
  check('the release reference is the offline one', merged.release_manifest.sha256 === offlineResults.release_manifest.sha256);
  check('every merged reference resolves with its hash', merged.test_results.every(row => [row.receipt, ...row.evidence].filter(Boolean)
    .every(item => existsSync(join(out, item.path)) && sha256File(join(out, item.path)) === item.sha256)));
  check('the inputs are untouched', sha256File(join(offline, 'results.json')) === offlineHash && sha256File(join(live, 'live-results.json')) === liveHash);
  check('the live results are kept beside the merge', existsSync(join(out, 'live-results.json')));
  check('earlier gate reports are not carried into the merge', !existsSync(join(out, 'gates-code_rc.json')) && existsSync(join(offline, 'gates-code_rc.json')));

  group('merge refusals');
  throws('an existing output directory is refused', () => mergeAcceptance({ base: offline, live, out }), /already exists/);
  const ownRun = join(root, 'live-own-run');
  liveRun(ownRun, 'live-2026-10-16');
  throws('a live run that did not join is refused', () => mergeAcceptance({ base: offline, live: ownRun, out: join(root, 'm2') }), /did not join/);
  check('a refused merge writes nothing', !existsSync(join(root, 'm2')));
  const otherRelease = join(root, 'live-other-release');
  liveRun(otherRelease, 'offline-1', { manifest: 'f'.repeat(64) });
  throws('another release is refused', () => mergeAcceptance({ base: offline, live: otherRelease, out: join(root, 'm3') }), /release manifest differs/);
  const tampered = join(root, 'live-tampered');
  liveRun(tampered, 'offline-1');
  writeFileSync(join(tampered, 'traces/LIVE-01.jsonl'), 'edited after the run\n');
  throws('a changed evidence file is refused', () => mergeAcceptance({ base: offline, live: tampered, out: join(root, 'm4') }), /hash changed/);
  const unknown = join(root, 'live-unknown');
  liveRun(unknown, 'offline-1', { caseId: 'LIVE-99' });
  throws('a case unknown to the offline results is refused', () => mergeAcceptance({ base: offline, live: unknown, out: join(root, 'm5') }), /unknown to the offline results/);
  const collide = join(root, 'live-collide');
  liveRun(collide, 'offline-1');
  write(join(collide, 'logs/OFF-01.log'), 'different');
  const collideResults = JSON.parse(readFileSync(join(collide, 'live-results.json'), 'utf8'));
  collideResults.test_results[0].evidence.push(ref(collide, 'logs/OFF-01.log', 'log'));
  writeFileSync(join(collide, 'live-results.json'), JSON.stringify(collideResults));
  throws('a path present in both runs with other content is refused', () => mergeAcceptance({ base: offline, live: collide, out: join(root, 'm6') }), /different content/);
  check('a refused collision writes nothing', !existsSync(join(root, 'm6')));
} catch (error) {
  failures.push(`unexpected: ${error.stack || error}`);
} finally {
  rmSync(root, { recursive: true, force: true });
}

console.log(JSON.stringify({ checks, failures, groups }));
process.exit(failures.length === 0 ? 0 : 1);
