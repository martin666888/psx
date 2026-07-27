// buildAgent.js — compile the Agent TS to a staging dir, then atomically
// replace wwwroot/js/agent-app once the compile is clean and the file set is
// complete. Never clears wwwroot/js/agent-app before a successful compile, so a
// broken build leaves the currently-shipping frontend untouched.
//
// Usage: node test/buildAgent.js

import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../../..');

// Resolve tsc through standard module resolution so it is found whether npm
// hoists TypeScript to the repo-root workspace node_modules or keeps it here.
const require = createRequire(import.meta.url);
const tsc = require.resolve('typescript/bin/tsc');
const tsconfig = path.join(repoRoot, 'frontend', 'agent', 'tsconfig.json');
const stagingDir = path.join(repoRoot, 'TestResults', 'web', 'agent-build-staging');
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

// Clean only our own staging dir; wwwroot stays untouched until success.
fs.rmSync(stagingDir, { recursive: true, force: true });

try {
  // tsconfig has noEmitOnError:true, so a type error yields no output + non-zero.
  execFileSync(process.execPath, [tsc, '-p', tsconfig], { stdio: 'inherit' });
} catch {
  console.error('[build:agent] tsc failed; wwwroot/js/agent-app left unchanged.');
  process.exit(1);
}

const files = collectFiles(stagingDir);
if (files.length === 0 || !files.includes('entry.js')) {
  console.error('[build:agent] staging output incomplete (missing entry.js); wwwroot unchanged.');
  process.exit(1);
}

// Recoverable replace: build a sibling temp dir, move the previous committed
// output aside, then promote the new output. If the final rename is blocked by
// antivirus/indexing, restore the previous output instead of leaving wwwroot
// without an Agent entry module.
const swapDir = targetDir + '.new';
const backupDir = targetDir + '.previous';
fs.rmSync(swapDir, { recursive: true, force: true });
fs.rmSync(backupDir, { recursive: true, force: true });
fs.cpSync(stagingDir, swapDir, { recursive: true });

let previousMoved = false;
try {
  if (fs.existsSync(targetDir)) {
    fs.renameSync(targetDir, backupDir);
    previousMoved = true;
  }
  fs.renameSync(swapDir, targetDir);
} catch (error) {
  try {
    if (fs.existsSync(targetDir)) fs.rmSync(targetDir, { recursive: true, force: true });
    if (previousMoved && fs.existsSync(backupDir)) fs.renameSync(backupDir, targetDir);
  } catch (rollbackError) {
    console.error('[build:agent] rollback failed; inspect wwwroot/js/agent-app.previous.', rollbackError);
  }
  fs.rmSync(swapDir, { recursive: true, force: true });
  console.error('[build:agent] could not promote the staged output; previous wwwroot output was restored.', error);
  process.exit(1);
}

if (previousMoved) fs.rmSync(backupDir, { recursive: true, force: true });

console.log(`[build:agent] wrote ${files.length} files to wwwroot/js/agent-app.`);
