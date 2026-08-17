import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { repositoryRoot } from './agentHarness.js';

// Provider-agnostic guardrail. The shared Agent frontend (core/ workspace/
// timeline/ composer/ plan/ history/ shell/ decisions/) must never name a
// provider or branch on a provider key: identity is data-driven through
// state.identity.assistantName and routing goes through the registry. Only the
// data-driven provider registry and the C# provider adapters may know brands.
// If a curated provider ever needs bespoke frontend behavior it must ship in
// its own provider module registered through the registry, not by editing
// shared code. This scan is mechanical so the rule cannot silently rot.

const SHARED_DIRS = ['core', 'workspace', 'timeline', 'composer', 'plan', 'history', 'shell', 'decisions'];
const SRC_ROOT = path.join(repositoryRoot, 'frontend', 'agent', 'src');

// Concrete provider/brand keys that must not be hardcoded in shared code.
// "cursor" is bounded so Tailwind `cursor-pointer` / `cursor-default` do not
// count as the Cursor product brand.
const BRAND_TOKENS =
  /\b(claude|anthropic|gemini|openai|chatgpt|codex|copilot|llama|qwen|kimi)\b|(?<![\w-])cursor(?![\w-])|\bgpt-\d/i;

// A per-provider-key business branch: comparing a provider identity to a
// string literal (e.g. providerId === 'claude'). Dynamic-vs-dynamic identity
// comparisons (assistantName !== this.state...assistantName) carry no literal
// and are intentionally allowed.
const KEY_BRANCH = /(provider(Id|Key|Name)?|assistantName)\s*[!=]==?\s*['"`]/i;

function collectSourceFiles(dir) {
  const out = [];
  const walk = (abs) => {
    for (const entry of fs.readdirSync(abs, { withFileTypes: true })) {
      const child = path.join(abs, entry.name);
      if (entry.isDirectory()) walk(child);
      else if (entry.name.endsWith('.ts') || entry.name.endsWith('.tsx')) out.push(child);
    }
  };
  if (fs.existsSync(dir)) walk(dir);
  return out;
}

const sharedFiles = SHARED_DIRS.flatMap((name) => collectSourceFiles(path.join(SRC_ROOT, name)));

test('guardrail: shared directories exist and contain sources to scan', () => {
  for (const name of SHARED_DIRS) {
    assert.ok(fs.existsSync(path.join(SRC_ROOT, name)), `shared dir missing: ${name}`);
  }
  assert.ok(sharedFiles.length > 0, 'expected shared TypeScript sources to scan');
  assert.ok(
    sharedFiles.some((file) => file.endsWith('.tsx')),
    'expected shared .tsx sources in the scan set'
  );
});

test('guardrail: shared frontend never names a concrete provider brand', () => {
  const offenders = [];
  for (const file of sharedFiles) {
    const lines = fs.readFileSync(file, 'utf8').split('\n');
    lines.forEach((line, index) => {
      if (BRAND_TOKENS.test(line)) {
        offenders.push(`${path.relative(SRC_ROOT, file).split(path.sep).join('/')}:${index + 1}: ${line.trim()}`);
      }
    });
  }
  assert.deepEqual(offenders, [], 'shared code must not hardcode a provider brand:\n' + offenders.join('\n'));
});

test('guardrail: shared frontend never branches on a provider key literal', () => {
  const offenders = [];
  for (const file of sharedFiles) {
    const lines = fs.readFileSync(file, 'utf8').split('\n');
    lines.forEach((line, index) => {
      if (KEY_BRANCH.test(line)) {
        offenders.push(`${path.relative(SRC_ROOT, file).split(path.sep).join('/')}:${index + 1}: ${line.trim()}`);
      }
    });
  }
  assert.deepEqual(offenders, [], 'shared code must not branch on a provider key literal:\n' + offenders.join('\n'));
});

test('template bootstrap copy stays provider-neutral', () => {
  const index = fs.readFileSync(path.join(repositoryRoot, 'frontend', 'webview', 'index.html'), 'utf8');
  assert.doesNotMatch(
    index,
    /claude|anthropic|gemini|openai|chatgpt|codex|copilot|(?<![\w-])cursor(?![\w-])|llama|qwen|kimi/iu
  );
});
