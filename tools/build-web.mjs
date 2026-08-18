// build-web.mjs — build the WebView frontend to a staging directory, validate
// it, then atomically replace wwwroot/app. A broken build never clears the
// committed output. Usage: node tools/build-web.mjs

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import {
  repoRoot,
  committedAppDir,
  runViteBuild,
  validateBundledAgentFonts,
  validateOutput
} from './web-build-lib.mjs';

const stagingDir = path.join(repoRoot, 'TestResults', 'web', 'vite-out-build');

const generate = spawnSync(process.execPath, [path.join(repoRoot, 'tools', 'generate-bridge-limits.mjs')], {
  cwd: repoRoot,
  stdio: 'inherit'
});
if (generate.status !== 0) process.exit(generate.status ?? 1);

runViteBuild(stagingDir, 'build:web');
validateBundledAgentFonts('build:web');
const files = validateOutput(stagingDir, 'build:web');

// Recoverable replace, mirroring the retired buildAgent.js: promote through a
// sibling temp dir so an interrupted swap can restore the previous output.
const swapDir = committedAppDir + '.new';
const backupDir = committedAppDir + '.previous';
fs.rmSync(swapDir, { recursive: true, force: true });
fs.rmSync(backupDir, { recursive: true, force: true });
fs.cpSync(stagingDir, swapDir, { recursive: true });

let previousMoved = false;
try {
  if (fs.existsSync(committedAppDir)) {
    fs.renameSync(committedAppDir, backupDir);
    previousMoved = true;
  }
  fs.renameSync(swapDir, committedAppDir);
} catch (error) {
  try {
    if (fs.existsSync(committedAppDir)) fs.rmSync(committedAppDir, { recursive: true, force: true });
    if (previousMoved && fs.existsSync(backupDir)) fs.renameSync(backupDir, committedAppDir);
  } catch (rollbackError) {
    console.error('[build:web] rollback failed; inspect wwwroot/app.previous.', rollbackError);
  }
  fs.rmSync(swapDir, { recursive: true, force: true });
  console.error('[build:web] could not promote the staged output; previous wwwroot/app was restored.', error);
  process.exit(1);
}

if (previousMoved) fs.rmSync(backupDir, { recursive: true, force: true });
console.log(`[build:web] wrote ${files.length} files to wwwroot/app.`);
