import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

// Vitest runs in the plain node environment on purpose: agentHarness.js
// installs a fresh jsdom window per test (same isolation contract as the old
// node --test runner), so a shared per-file jsdom environment would fight the
// harness. Tests import the Agent TS sources under frontend/agent/src directly;
// the shipped bundle is produced by Vite into wwwroot/app and guarded by
// verify:web.
export default defineConfig({
  // frontend/agent has no local tsconfig anymore (typecheck:web owns types via
  // frontend/webview/tsconfig.json), so pin the automatic JSX runtime here
  // instead of relying on esbuild's tsconfig discovery.
  esbuild: {
    jsx: 'automatic',
    jsxImportSource: 'react'
  },
  resolve: {
    alias: {
      '@agent-src': path.join(repoRoot, 'frontend', 'agent', 'src'),
      // Match the production Vite/tsconfig "@/..." mapping (shadcn imports).
      '@': path.join(repoRoot, 'frontend', 'agent', 'src')
    }
  },
  test: {
    environment: 'node',
    include: ['test/**/*.test.js'],
    setupFiles: ['test/vitest.setup.js'],
    // The harness mutates globalThis (window/document/Bridge); keep every
    // file isolated like node --test did. Run exactly one worker/file at a
    // time: parallel jsdom/React roots have previously exhausted workstation
    // memory and left orphaned Node workers after a failed test.
    isolate: true,
    pool: 'forks',
    fileParallelism: false,
    maxWorkers: 1,
    // A broken jsdom/focus loop must fail the worker instead of exhausting
    // physical memory and freezing the developer workstation.
    execArgv: ['--max-old-space-size=1024'],
    testTimeout: 20000,
    coverage: {
      provider: 'v8',
      enabled: false,
      include: ['frontend/agent/src/**'],
      allowExternal: true,
      reporter: ['text', 'json-summary', 'cobertura'],
      reportsDirectory: path.join(repoRoot, 'TestResults', 'web', 'coverage')
    }
  }
});
