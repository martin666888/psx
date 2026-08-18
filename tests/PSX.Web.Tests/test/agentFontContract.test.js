// agentFontContract.test.js — static typography ownership and packaging guards.

import { describe, it } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { repositoryRoot } from './agentHarness.js';

const webview = path.join(repositoryRoot, 'frontend', 'webview');
const agent = path.join(repositoryRoot, 'frontend', 'agent', 'src');
const uiStack = 'Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Segoe UI Emoji, sans-serif';
const monoStack = 'PSX Maple Mono, Segoe UI Emoji, Microsoft YaHei UI, monospace';

function read(...segments) {
  return fs.readFileSync(path.join(repositoryRoot, ...segments), 'utf8');
}

function walk(dir, extension) {
  const files = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const absolute = path.join(dir, entry.name);
    if (entry.isDirectory()) files.push(...walk(absolute, extension));
    else if (absolute.endsWith(extension)) files.push(absolute);
  }
  return files;
}

function readCommittedMainCss() {
  const appRoot = path.join(repositoryRoot, 'wwwroot', 'app');
  const manifest = JSON.parse(fs.readFileSync(path.join(appRoot, '.vite', 'manifest.json'), 'utf8'));
  const entry = Object.entries(manifest).find(([key, value]) => key.endsWith('index.html') && value.isEntry)?.[1];
  assert.ok(entry?.css?.length, 'production entry CSS is missing from the Vite manifest');
  return entry.css.map((relative) => fs.readFileSync(path.join(appRoot, relative), 'utf8')).join('\n');
}

describe('bundled Agent typography contract', () => {
  it('maps the unmodified Maple Mono faces to real 400 and 600 weights', () => {
    const css = read('frontend', 'webview', 'src', 'css', 'agent', 'tokens.css');
    assert.match(css, /MapleMonoNormal-CN-Regular\.ttf[\s\S]*?font-weight:\s*400/);
    assert.match(css, /MapleMonoNormal-CN-SemiBold\.ttf[\s\S]*?font-weight:\s*600/);
    assert.match(css, /\[data-agent-font-surface\][\s\S]*?font-synthesis:\s*style/);
    assert.match(css, /:is\(strong, b\)[\s\S]*?\.font-bold[\s\S]*?font-weight:\s*600/);
  });

  it('keeps workspace chrome and diagnostics on the system UI token', () => {
    for (const relative of ['workspace-chrome.css', 'runtime-diagnostics.css']) {
      const css = fs.readFileSync(path.join(webview, 'src', 'css', relative), 'utf8');
      assert.ok(css.includes('var(--workspace-font-ui)'), relative);
      assert.ok(!css.includes('var(--agent-font-ui)'), relative);
      assert.ok(!css.includes('var(--agent-font-mono)'), relative);
    }
    const allCss = walk(path.join(webview, 'src', 'css'), '.css')
      .map((file) => fs.readFileSync(file, 'utf8'))
      .join('\n');
    assert.ok(!/\.agent-ui\s*\{[^}]*font-family/s.test(allCss));
  });

  it('marks every Agent root and portalled content surface explicitly', () => {
    assert.match(read('frontend', 'webview', 'index.html'), /class="agent-panel" data-agent-font-surface/);
    const required = [
      ['history', 'HistoryDockView.tsx'],
      ['usage', 'UsagePanel.tsx'],
      ['components', 'ui', 'dialog.tsx'],
      ['components', 'ui', 'dropdown-menu.tsx'],
      ['components', 'ui', 'hover-card.tsx'],
      ['components', 'ui', 'popover.tsx'],
      ['components', 'ui', 'select.tsx'],
      ['components', 'ui', 'tooltip.tsx']
    ];
    for (const segments of required) {
      assert.ok(read('frontend', 'agent', 'src', ...segments).includes('data-agent-font-surface'));
    }
    const tokens = read('frontend', 'webview', 'src', 'css', 'agent', 'tokens.css');
    assert.match(tokens, /:root\s*\{[\s\S]*?--psx-font-sans:\s*var\(--workspace-font-ui\)/);
    assert.match(tokens, /:root\s*\{[\s\S]*?--psx-font-mono:\s*var\(--workspace-font-mono\)/);
    assert.match(tokens, /\[data-agent-font-surface\]\s*\{[\s\S]*?--psx-font-sans:\s*var\(--agent-font-ui\)/);
    assert.match(tokens, /\[data-agent-font-surface\]\s*\{[\s\S]*?--psx-font-mono:\s*var\(--agent-font-mono\)/);

    const tailwind = read('frontend', 'webview', 'src', 'css', 'tailwind.css');
    assert.ok(tailwind.includes('--font-sans: var(--psx-font-sans)'));
    assert.ok(tailwind.includes('--font-mono: var(--psx-font-mono)'));
    assert.ok(!tailwind.includes('--font-sans: var(--agent-font-ui)'));
    assert.ok(!tailwind.includes('--font-mono: var(--agent-font-mono)'));
  });

  it('keeps Tailwind Preflight on workspace fonts and scopes utilities to their surface', () => {
    const css = readCommittedMainCss();
    assert.match(css, /--default-font-family:var\(--psx-font-sans\)/);
    assert.match(css, /--default-mono-font-family:var\(--psx-font-mono\)/);
    assert.match(css, /\.font-sans\{font-family:var\(--psx-font-sans\)\}/);
    assert.match(css, /\.font-mono\{font-family:var\(--psx-font-mono\)\}/);
    assert.ok(!css.includes('--default-font-family:var(--agent-font-ui)'));
    assert.ok(!css.includes('--default-mono-font-family:var(--agent-font-mono)'));
  });

  it('separates the proportional UI and bundled mono stacks with no first-party 700 utility', () => {
    const iniFiles = [
      path.join(repositoryRoot, 'psx.ini'),
      ...fs.readdirSync(path.join(repositoryRoot, 'theme-presets'))
        .filter((name) => name.endsWith('.ini'))
        .map((name) => path.join(repositoryRoot, 'theme-presets', name))
    ];
    for (const file of iniFiles) {
      const source = fs.readFileSync(file, 'utf8');
      assert.ok(source.includes(`fontFamily=${uiStack}`), file);
      assert.ok(source.includes(`monoFontFamily=${monoStack}`), file);
    }
    const offenders = walk(agent, '.tsx')
      .filter((file) => fs.readFileSync(file, 'utf8').includes('font-bold'));
    assert.deepEqual(offenders, []);
  });

  it('keeps natural-language surfaces proportional and technical content mono', () => {
    const tokens = read('frontend', 'webview', 'src', 'css', 'agent', 'tokens.css');
    assert.ok(tokens.includes(`--agent-font-ui: "Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", "Segoe UI Emoji", sans-serif`));
    assert.ok(tokens.includes(`--agent-font-mono: "PSX Maple Mono", "Segoe UI Emoji", "Microsoft YaHei UI", monospace`));

    const monoCss = [
      read('frontend', 'webview', 'src', 'css', 'agent', 'markdown.css'),
      read('frontend', 'webview', 'src', 'css', 'agent', 'messages.css')
    ].join('\n');
    assert.match(monoCss, /(?:code|pre|kbd|tool)[\s\S]*?font-family:\s*var\(--agent-font-mono\)/);

    const composer = read('frontend', 'agent', 'src', 'composer', 'ComposerView.tsx');
    assert.match(composer, /<PromptInputTextarea[\s\S]*?className="[^"]*font-sans/);
  });
});
