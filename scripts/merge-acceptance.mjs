// Combines an offline acceptance run and the live run that joined it into one evidence directory for
// evaluate_gates.py --profile workflow_release.
//
//   node scripts/merge-acceptance.mjs --base <offline-evidence-dir> --live <live-evidence-dir> --out <new-dir>
//
// Refuses when the live run did not join the offline run (run live-acceptance.mjs with --join-results), when the
// candidate or release differ, when any referenced file is missing or changed, or when <out> already exists.
// Both input directories are left untouched.
import { resolve } from 'node:path';
import { argument } from './release-candidate.mjs';
import { mergeAcceptance } from './acceptance-evidence.mjs';

const required = name => { const value = argument(name); if (!value) throw new Error(`${name} is required`); return resolve(value); };
try {
  const summary = mergeAcceptance({ base: required('--base'), live: required('--live'), out: required('--out') });
  process.stdout.write(JSON.stringify(summary, null, 2) + '\n');
} catch (error) {
  process.stderr.write(`${error.message}\n`);
  process.exit(4);
}
