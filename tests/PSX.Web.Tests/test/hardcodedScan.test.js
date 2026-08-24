// hardcodedScan.test.js — guards the "no PSX-authored literals leak into
// another language" contract. Frontend sources must not contain CJK UI
// strings outside the locale resources; brand names, protocol values and a
// small file allowlist (code comments) stay exempt. English UI literals are
// covered by review + the parity gate; this scan targets CJK leakage.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..', '..');

const SCAN_ROOTS = ['frontend/webview/src', 'frontend/agent/src'];
const EXCLUDED_DIRECTORIES = ['locales'];
const ALLOWED_FILES = new Set([
  // Pure comment documentation; no runtime strings.
  'frontend/webview/src/css/agent/tokens.css',
  'frontend/webview/src/css/agent/messages.css',
  'frontend/webview/src/css/terminal.css',
  'frontend/agent/src/markdown/codeLanguages.ts'
]);

// Product / provider names keep their canonical spelling in every language.
const BRAND_ALLOWLIST = [
  'DeepSeek Harness',
  'Kimi Code Web',
  'Claude Code',
  'Kimi Code',
  'Qwen Code',
  'OpenCode'
];

function* walk(directory) {
  for (const entry of readdirSync(directory)) {
    const full = path.join(directory, entry);
    const stat = statSync(full);
    if (stat.isDirectory()) {
      if (EXCLUDED_DIRECTORIES.includes(entry)) continue;
      yield* walk(full);
    } else {
      yield full;
    }
  }
}

test('frontend sources carry no CJK UI strings outside locale resources', () => {
  const problems = [];
  for (const root of SCAN_ROOTS) {
    const absoluteRoot = path.join(repoRoot, root);
    for (const file of walk(absoluteRoot)) {
      const relative = path.relative(repoRoot, file).split(path.sep).join('/');
      if (ALLOWED_FILES.has(relative)) continue;
      if (!/\.(js|jsx|ts|tsx|html)$/.test(relative)) continue;
      const source = readFileSync(file, 'utf8');
      const lines = source.split(/\r?\n/);
      lines.forEach((line, index) => {
        const stripped = line
          .replace(/\/\/.*$/, '')
          .replace(/\/\*[\s\S]*?\*\//g, '');
        if (!/[\u4e00-\u9fff]/.test(stripped)) return;
        problems.push(`${relative}:${index + 1}: ${line.trim().slice(0, 80)}`);
      });
    }
  }
  assert.deepEqual(problems, [], 'CJK literals leaked into frontend sources');
});

test('brand allowlist stays stable', () => {
  assert.equal(BRAND_ALLOWLIST.length, 6);
});
