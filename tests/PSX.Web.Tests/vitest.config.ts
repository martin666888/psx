import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

// Vitest runs in the plain node environment on purpose: agentHarness.js
// installs a fresh jsdom window per test (same isolation contract as the old
// node --test runner), so a shared per-file jsdom environment would fight the
// harness. Tests import the Agent TS sources under frontend/agent/src directly;
// the committed tsc output under wwwroot/js/agent-app stays the shipped asset
// and is still guarded by verify:agent until the Vite entry point ships.
export default defineConfig({
  resolve: {
    alias: {
      '@agent-src': path.join(repoRoot, 'frontend', 'agent', 'src')
    }
  },
  test: {
    environment: 'node',
    include: ['test/**/*.test.js'],
    setupFiles: ['test/vitest.setup.js'],
    // The harness mutates globalThis (window/document/Bridge); keep every
    // file in its own isolated worker like node --test did.
    isolate: true,
    pool: 'forks',
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
