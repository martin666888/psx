// reactIslandGuard.test.js — source-level guards for the lazy React islands.
//
// The Vite build owns bundling (verify:web pins the produced chunks and the
// single-React-instance invariant), so these guards run against the TypeScript
// sources instead of committed vendor bundles: React must stay confined to the
// island modules, controllers must reach islands only through dynamic import
// (that is what keeps React out of the terminal-only startup path), and the
// migration seams removed at cutover must not resurface.

import { describe, it } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { repositoryRoot } from './agentHarness.js';

const agentSrc = path.join(repositoryRoot, 'frontend', 'agent', 'src');

// Collect every static value import, re-export-from and dynamic import
// specifier. `import type { ... } from 'react'` is erased by the compiler and
// pulls nothing at runtime, so type-only imports are excluded on purpose.
function importSpecifiers(source) {
  const withoutTypeImports = source.replace(/import\s+type\s[^;]*;/g, '');
  const specifiers = [];
  const patterns = [
    /(?:^|[;\s{}()])import\s*(?:[\w$*{},\s]+?from\s*)?["']([^"']+)["']/g,
    /(?:^|[;\s{}()])export\s*(?:\*|\{[^}]*\})\s*from\s*["']([^"']+)["']/g,
    /import\(\s*["']([^"']+)["']\s*\)/g
  ];
  for (const pattern of patterns) {
    for (const match of withoutTypeImports.matchAll(pattern)) specifiers.push(match[1]);
  }
  return [...new Set(specifiers)];
}

function walkFiles(dir, filter) {
  const files = [];
  const walk = (current) => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const abs = path.join(current, entry.name);
      if (entry.isDirectory()) walk(abs);
      else if (filter.test(entry.name)) files.push(abs);
    }
  };
  walk(dir);
  return files;
}

describe('lazy React-island loading guard', () => {
  // The only source modules allowed to import React at runtime. JSX components
  // only reach react/jsx-runtime through the compiler, so listing the island
  // mount modules plus the components with explicit hook/createElement imports
  // covers the full runtime surface.
  const island = [
    'workspace/runtimeIsland.ts',
    'workspace/SessionRuntimeCard.tsx',
    'workspace/sessionIsland.ts',
    'workspace/SessionToolbar.tsx',
    'plan/planIsland.ts',
    'plan/PlanCard.tsx',
    'history/historyIsland.ts',
    'history/HistoryList.tsx',
    'composer/composerIsland.ts',
    'composer/ComposerBits.tsx',
    'timeline/timelineIsland.ts',
    'timeline/TimelineView.tsx',
    'timeline/TimelineDecisions.tsx',
    'core/reactIsland.tsx',
    'ui/Psx.tsx'
  ];

  it('keeps runtime React imports confined to the island modules', () => {
    const offenders = [];
    for (const abs of walkFiles(agentSrc, /\.(?:ts|tsx)$/)) {
      const relative = path.relative(agentSrc, abs).replaceAll('\\', '/');
      // components/ = the shadcn/AI Elements component library (CP2+): React
      // components by definition, reachable only through the island modules
      // below, so they never widen the terminal-only startup path.
      if (relative.startsWith('components/')) continue;
      const bare = importSpecifiers(fs.readFileSync(abs, 'utf8')).filter((s) =>
        /^react(-dom)?(\/|$)/.test(s)
      );
      if (bare.length > 0 && !island.includes(relative)) offenders.push(relative);
    }
    assert.deepEqual(offenders, []);
  });

  it('reaches the island only through a dynamic import', () => {
    const controllers = [
      ['workspace/SessionRuntimeController.ts', 'runtimeIsland'],
      ['workspace/SessionRuntimeController.ts', 'sessionIsland'],
      ['plan/PlanController.ts', 'planIsland'],
      ['history/HistoryDockController.ts', 'historyIsland'],
      ['composer/ComposerController.ts', 'composerIsland'],
      ['timeline/TimelineController.ts', 'timelineIsland']
    ];
    for (const [controller, islandModule] of controllers) {
      const source = fs.readFileSync(path.join(agentSrc, controller.replaceAll('/', path.sep)), 'utf8');
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

  it('contains no migration switch, fallback seam, or direct vendor bypass', () => {
    const forbidden = [
      'psx.agent.experimental.react',
      'isReactUiEnabled',
      'reactUiMode',
      'onLoadFailed',
      'hasReactFailed',
      'replayLegacyEvent',
      'replayDecisionEvent',
      'runtime-card-legacy'
    ];
    const findings = [];
    const roots = [agentSrc, path.join(repositoryRoot, 'frontend', 'webview', 'src')];
    for (const root of roots) {
      for (const abs of walkFiles(root, /\.(?:ts|tsx|js)$/)) {
        const source = fs.readFileSync(abs, 'utf8');
        for (const token of forbidden) {
          if (source.includes(token)) {
            findings.push(path.relative(repositoryRoot, abs).replaceAll('\\', '/') + ': ' + token);
          }
        }
        if (/vendor\/react\/.+\.js/.test(source)) {
          findings.push(path.relative(repositoryRoot, abs).replaceAll('\\', '/') + ': direct vendor import');
        }
      }
    }
    assert.deepEqual(findings, []);
  });
});
