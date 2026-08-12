// verify-web.mjs — independently rebuild the WebView frontend into a scratch
// directory and byte-compare it with the committed wwwroot/app. Read-only with
// respect to the committed output; the permanent freshness gate since CP1b.
// Usage: node tools/verify-web.mjs

import path from 'node:path';
import { spawnSync } from 'node:child_process';
import {
  repoRoot,
  committedAppDir,
  runViteBuild,
  validateBundledAgentFonts,
  validateOutput,
  compareDirectories
} from './web-build-lib.mjs';

const scratchDir = path.join(repoRoot, 'TestResults', 'web', 'vite-out-verify');

const generated = spawnSync(
  process.execPath,
  [path.join(repoRoot, 'tools', 'generate-bridge-limits.mjs'), '--check'],
  { cwd: repoRoot, stdio: 'inherit' }
);
if (generated.status !== 0) process.exit(generated.status ?? 1);

runViteBuild(scratchDir, 'verify:web');
validateBundledAgentFonts('verify:web');
const files = validateOutput(scratchDir, 'verify:web');
validateOutput(committedAppDir, 'verify:web(committed)');

const differences = compareDirectories(committedAppDir, scratchDir);
if (differences.length > 0) {
  console.error('[verify:web] committed wwwroot/app is stale:\n  - ' + differences.join('\n  - '));
  console.error('[verify:web] run `npm run build:web` and commit the output.');
  process.exit(1);
}
console.log(`[verify:web] wwwroot/app matches a fresh build (${files.length} files).`);
