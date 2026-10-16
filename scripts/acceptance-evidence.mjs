// Evidence helpers for the live acceptance harness and merge-acceptance.mjs.
//
// The gate evaluator binds every PASS receipt to the single run_id of results.json. A live run therefore joins the
// offline run of the same candidate and release (joinedRunId) instead of starting its own; mergeAcceptance then
// combines the two evidence directories into a NEW directory. Nothing here rewrites a receipt: a live run that did not
// join the offline run cannot be merged.
import { constants as fsConstants, copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve, sep } from 'node:path';
import { sha256File } from './release-candidate.mjs';

function readJson(path) { return JSON.parse(readFileSync(path, 'utf8')); }

function inside(root, relative) {
  const full = resolve(root, relative);
  if (!full.startsWith(resolve(root) + sep)) throw new Error(`evidence path escapes its root: ${relative}`);
  return full;
}

// Returns the offline run_id a live run must use, or throws when the base run is not for this candidate and release.
export function joinedRunId(baseResults, { candidateSha, manifestSha256 }) {
  const problems = [];
  if (typeof baseResults?.run_id !== 'string' || !baseResults.run_id) problems.push('base results have no run_id');
  if (baseResults?.candidate_sha !== candidateSha) problems.push('base results are for another candidate');
  if (baseResults?.source_dirty !== false) problems.push('base results come from a dirty source tree');
  if (baseResults?.release_manifest?.sha256 !== manifestSha256) problems.push('base results reference another release manifest');
  if (problems.length) throw new Error(`cannot join the offline run: ${problems.join('; ')}`);
  return baseResults.run_id;
}

// Copies a produced file into <evidence>/artifacts/<name> and returns its raw-evidence reference.
// expectedSha256, when given, must match the source (for files whose hash the host already reported).
export function attachArtifact(evidence, source, name, kind = 'file_artifact', expectedSha256 = null) {
  if (!existsSync(source)) throw new Error(`artifact not found: ${source}`);
  const path = `artifacts/${name}`;
  const target = inside(evidence, path);
  if (existsSync(target)) throw new Error(`artifact ${path} already exists; evidence is never overwritten`);
  mkdirSync(dirname(target), { recursive: true });
  copyFileSync(source, target, fsConstants.COPYFILE_EXCL);
  const sha256 = sha256File(target);
  if (expectedSha256 && sha256 !== expectedSha256) throw new Error(`artifact ${name} hash ${sha256} differs from the reported ${expectedSha256}`);
  return { path, sha256, kind };
}

function verifyRefs(root, row) {
  for (const ref of [row.receipt, ...(row.evidence ?? [])].filter(Boolean)) {
    const file = inside(root, ref.path);
    if (!existsSync(file) || sha256File(file) !== ref.sha256) throw new Error(`${row.case_id}: ${ref.path} is missing or its hash changed`);
  }
}

// Merges <base>/results.json (offline) and <live>/live-results.json into a new directory <out>.
export function mergeAcceptance({ base, live, out }) {
  const baseResults = readJson(join(base, 'results.json'));
  const liveResults = readJson(join(live, 'live-results.json'));
  const problems = [];
  if (liveResults.run_id !== baseResults.run_id) problems.push(`live run ${liveResults.run_id} did not join offline run ${baseResults.run_id} (re-run the live harness with --join-results)`);
  if (liveResults.candidate_sha !== baseResults.candidate_sha) problems.push('candidate SHA differs');
  if (liveResults.release_manifest_sha256 !== baseResults.release_manifest?.sha256) problems.push('release manifest differs');
  if (!liveResults.runtime) problems.push('live run has no runtime facts');
  if (existsSync(out)) problems.push(`output directory already exists: ${out}`);
  if (problems.length) throw new Error(`refusing to merge: ${problems.join('; ')}`);

  for (const row of baseResults.test_results) verifyRefs(base, row);
  for (const row of liveResults.test_results) verifyRefs(live, row);
  const baseIds = new Set(baseResults.test_results.map(row => row.case_id));
  const liveRefs = liveResults.test_results.flatMap(row => [row.receipt, ...(row.evidence ?? [])].filter(Boolean));
  for (const row of liveResults.test_results) {
    if (!baseIds.has(row.case_id)) throw new Error(`refusing to merge: live case ${row.case_id} is unknown to the offline results`);
  }
  for (const ref of liveRefs) {
    const clash = inside(base, ref.path);
    if (existsSync(clash) && sha256File(clash) !== ref.sha256) throw new Error(`refusing to merge: ${ref.path} exists in both runs with different content`);
  }

  cpSync(base, out, { recursive: true, errorOnExist: true, force: false });
  for (const ref of liveRefs) {
    const target = inside(out, ref.path);
    mkdirSync(dirname(target), { recursive: true });
    copyFileSync(inside(live, ref.path), target);
  }
  copyFileSync(join(live, 'live-results.json'), join(out, 'live-results.json'));

  const liveRows = new Map(liveResults.test_results.map(row => [row.case_id, row]));
  const merged = { ...baseResults, runtime: liveResults.runtime,
    test_results: baseResults.test_results.map(row => liveRows.get(row.case_id) ?? row) };
  writeFileSync(join(out, 'results.json'), JSON.stringify(merged, null, 2) + '\n');
  const counts = {};
  for (const row of merged.test_results) counts[row.status] = (counts[row.status] ?? 0) + 1;
  return { run_id: merged.run_id, replaced: liveRows.size, total: merged.test_results.length, counts };
}
