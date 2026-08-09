import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

// Vitest runs in the plain node environment on purpose: agentHarness.js
// installs a fresh jsdom window per test, so a shared per-file jsdom
// environment would fight the harness. Tests import the Agent TS sources under
// frontend/agent/src directly;
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
      '@': path.join(repoRoot, 'frontend', 'agent', 'src'),
      // The lucide-react barrel expands the package's entire ~1,900-icon
      // module graph. V8 precise coverage retains external buffers for that
      // graph even though production Vite tree-shakes it. UI tests only depend
      // on the icons' SVG/component semantics; use a tiny local implementation
      // and leave the real package to build:web + verify:web.
      'lucide-react': path.join(here, 'test', 'lucideReactShim.js')
    }
  },
  test: {
    environment: 'node',
    include: ['test/**/*.test.js'],
    setupFiles: ['test/vitest.setup.js'],
    reporters: [
      'default',
      ['json', {
        outputFile: path.join(repoRoot, 'TestResults', 'web', 'test-summary.json')
      }]
    ],
    // Each file needs a fresh module graph because Agent modules capture the
    // active jsdom/React root. A single isolated worker thread preserves that
    // boundary without retaining one child process per file under V8 coverage.
    isolate: true,
    pool: 'threads',
    fileParallelism: false,
    maxWorkers: 1,
    // tools/run-guarded-vitest.ps1 places the entire runner under hard
    // per-process and process-tree Job Object memory limits.
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
