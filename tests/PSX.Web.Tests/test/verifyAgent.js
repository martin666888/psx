// verifyAgent.js — gate that the committed wwwroot/js/agent-app exactly matches
// a fresh compile of frontend/agent/src. Compiles with tsconfig.verify.json
// (identical compiler options, separate outDir) into a throwaway dir, then
// compares file set + byte content against wwwroot/js/agent-app. Any drift
// (stale artifact, forgotten build:agent, CRLF leak) exits non-zero.
//
// Usage: node test/verifyAgent.js

import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../../..');
const pkgRoot = path.resolve(here, '..');

const tsc = path.join(pkgRoot, 'node_modules', 'typescript', 'bin', 'tsc');
const tsconfig = path.join(repoRoot, 'frontend', 'agent', 'tsconfig.verify.json');
const verifyDir = path.join(repoRoot, 'TestResults', 'web', 'agent-verify');
const targetDir = path.join(repoRoot, 'wwwroot', 'js', 'agent-app');

function collectFiles(dir) {
  const out = [];
  const walk = (rel) => {
    const abs = path.join(dir, rel);
    for (const entry of fs.readdirSync(abs, { withFileTypes: true })) {
      const childRel = rel ? path.join(rel, entry.name) : entry.name;
      if (entry.isDirectory()) walk(childRel);
      else out.push(childRel.split(path.sep).join('/'));
    }
  };
  if (fs.existsSync(dir)) walk('');
  return out.sort();
}

fs.rmSync(verifyDir, { recursive: true, force: true });

try {
  execFileSync(process.execPath, [tsc, '-p', tsconfig], { stdio: 'inherit' });
} catch {
  console.error('[verify:agent] tsc failed to compile frontend/agent/src.');
  process.exit(1);
}

const fresh = collectFiles(verifyDir);
const committed = collectFiles(targetDir);

const drift = [];
const freshSet = new Set(fresh);
const committedSet = new Set(committed);
for (const f of fresh) if (!committedSet.has(f)) drift.push(`missing in wwwroot: ${f}`);
for (const f of committed) if (!freshSet.has(f)) drift.push(`unexpected in wwwroot: ${f}`);

for (const f of fresh) {
  if (!committedSet.has(f)) continue;
  const a = fs.readFileSync(path.join(verifyDir, f));
  const b = fs.readFileSync(path.join(targetDir, f));
  if (!a.equals(b)) drift.push(`content differs: ${f}`);
}

if (drift.length > 0) {
  console.error('[verify:agent] wwwroot/js/agent-app is out of date. Run `npm run build:agent` and commit.');
  for (const d of drift) console.error('  - ' + d);
  process.exit(1);
}

console.log(`[verify:agent] wwwroot/js/agent-app matches source (${committed.length} files).`);
