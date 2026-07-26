// vendorReact.test.js — static guards for the vendored React ESM bundles.
//
// wwwroot/vendor/react is generated only by `npm run vendor:react` and then
// committed; these tests pin the committed bytes to vendor-manifest.json and
// assert the import surface of every file, so the bundles stay offline,
// self-contained and single-instance (everything funnels into react-core.js).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { repositoryRoot } from './agentHarness.js';

const require = createRequire(import.meta.url);
const vendorDir = path.join(repositoryRoot, 'wwwroot', 'vendor', 'react');
const manifest = JSON.parse(fs.readFileSync(path.join(vendorDir, 'vendor-manifest.json'), 'utf8'));

// Collect every static import, re-export-from and dynamic import specifier.
function importSpecifiers(source) {
  const specifiers = [];
  const patterns = [
    /(?:^|[;\s{}()])import\s*(?:[\w$*{},\s]+?from\s*)?["']([^"']+)["']/g,
    /(?:^|[;\s{}()])export\s*(?:\*|\{[^}]*\})\s*from\s*["']([^"']+)["']/g,
    /import\(\s*["']([^"']+)["']\s*\)/g
  ];
  for (const pattern of patterns) {
    for (const match of source.matchAll(pattern)) specifiers.push(match[1]);
  }
  return [...new Set(specifiers)];
}

describe('vendored React bundles', () => {
  it('matches the pinned React and esbuild versions', () => {
    assert.equal(manifest.reactVersion, require('react/package.json').version);
    assert.equal(manifest.reactVersion, require('react-dom/package.json').version);
    assert.equal(manifest.esbuildVersion, require('esbuild/package.json').version);
  });

  it('lists exactly the five committed bundle files', () => {
    const committed = fs.readdirSync(vendorDir).filter((f) => f.endsWith('.js')).sort();
    assert.deepEqual(committed, manifest.files.map((f) => f.file).sort());
  });

  it('hashes every committed file to the manifest sha256 and byte size', () => {
    for (const entry of manifest.files) {
      const contents = fs.readFileSync(path.join(vendorDir, entry.file));
      assert.equal(contents.length, entry.bytes, entry.file + ' byte size drifted');
      assert.equal(
        createHash('sha256').update(contents).digest('hex'),
        entry.sha256,
        entry.file + ' contents drifted from vendor-manifest.json; rerun npm run vendor:react'
      );
    }
  });

  it('keeps the import surface of every file to the manifest declaration', () => {
    for (const entry of manifest.files) {
      const source = fs.readFileSync(path.join(vendorDir, entry.file), 'utf8');
      const specifiers = importSpecifiers(source);
      assert.deepEqual(specifiers.sort(), [...entry.imports].sort(), entry.file + ' import surface changed');
      for (const specifier of specifiers) {
        assert.ok(specifier.startsWith('./'), entry.file + ' must only import sibling files: ' + specifier);
        assert.ok(!/^https?:/.test(specifier), entry.file + ' must not import from the network');
      }
    }
  });

  it('exposes the full named export surface through the facades in plain Node', async () => {
    // Importing the committed files directly (no bundler, no import map)
    // proves they are offline and self-contained: any unresolved or remote
    // specifier would throw here.
    const url = (file) => new URL('file:///' + path.join(vendorDir, file).replaceAll('\\', '/').replaceAll('#', '%23'));
    const react = await import(url('react.js'));
    const jsxRuntime = await import(url('react-jsx-runtime.js'));
    const reactDom = await import(url('react-dom.js'));
    const reactDomClient = await import(url('react-dom-client.js'));
    assert.equal(react.version, manifest.reactVersion);
    assert.equal(typeof react.useState, 'function');
    assert.equal(typeof jsxRuntime.jsx, 'function');
    assert.equal(typeof reactDom.flushSync, 'function');
    assert.equal(typeof reactDomClient.createRoot, 'function');
  });
});

describe('flag-off React zero-load guard', () => {
  const agentApp = path.join(repositoryRoot, 'wwwroot', 'js', 'agent-app');
  // The only compiled modules allowed to statically import React.
  const island = [
    'workspace/runtimeIsland.js',
    'workspace/SessionRuntimeCard.js',
    'workspace/sessionIsland.js',
    'workspace/SessionToolbar.js',
    'plan/planIsland.js',
    'plan/PlanCard.js',
    'history/historyIsland.js',
    'history/HistoryList.js'
  ];

  it('keeps static React imports confined to the island modules', () => {
    const offenders = [];
    const walk = (dir) => {
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const abs = path.join(dir, entry.name);
        if (entry.isDirectory()) {
          walk(abs);
        } else if (entry.name.endsWith('.js')) {
          const relative = path.relative(agentApp, abs).replaceAll('\\', '/');
          const bare = importSpecifiers(fs.readFileSync(abs, 'utf8')).filter((s) => /^react(-dom)?(\/|$)/.test(s));
          if (bare.length > 0 && !island.includes(relative)) offenders.push(relative);
        }
      }
    };
    walk(agentApp);
    assert.deepEqual(offenders, []);
  });

  it('reaches the island only through a dynamic import', () => {
    const controllers = [
      ['workspace/SessionRuntimeController.js', 'runtimeIsland'],
      ['workspace/SessionRuntimeController.js', 'sessionIsland'],
      ['plan/PlanController.js', 'planIsland'],
      ['history/HistoryDockController.js', 'historyIsland']
    ];
    for (const [controller, islandModule] of controllers) {
      const source = fs.readFileSync(path.join(agentApp, controller.replaceAll('/', path.sep)), 'utf8');
      assert.ok(
        !new RegExp("import[^;]*from\\s*['\"]\\./" + islandModule + "\\.js['\"]").test(source),
        controller + ': static island import found'
      );
      assert.ok(
        source.includes("import('./" + islandModule + ".js')"),
        controller + ': dynamic island import missing'
      );
    }
  });
});
