import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

const here = path.dirname(fileURLToPath(import.meta.url));
const agentSrc = path.resolve(here, '..', 'agent', 'src');
const productionCsp = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob: https://psx-attachments.local",
  "font-src 'self' data:",
  "connect-src 'self'",
  "media-src 'self' blob: https://psx-attachments.local",
  "worker-src 'self' blob:",
  "object-src 'none'",
  "frame-src 'none'",
  "base-uri 'none'",
  "form-action 'none'"
].join('; ');

function cspPlugin(development: boolean) {
  const csp = development
    ? productionCsp
      .replace("script-src 'self'", "script-src 'self' 'unsafe-inline'")
      .replace(
        "connect-src 'self'",
        "connect-src 'self' http://127.0.0.1:5199 http://localhost:5199 ws://127.0.0.1:5199 ws://localhost:5199"
      )
    : productionCsp;
  return {
    name: 'psx-content-security-policy',
    transformIndexHtml(html: string): string {
      return html.replace('__PSX_CSP__', csp);
    }
  };
}

// Production WebView contract (AGENTS.md + campaign plan):
// - served from the psx.local virtual host under /app/ (offline, no CDN)
// - target es2022, no sourcemaps, hashed file names, committed output
// - the Agent app stays a dynamic chunk behind import('../../agent/src/entry.ts')
//   so the Terminal first screen never pays for React or the Agent UI
// - build/verify tooling reads .vite/manifest.json to validate the output
export default defineConfig(({ command }) => ({
  // Root is this package regardless of the caller's cwd (npm run dev:web runs
  // from the repo root; the build tooling sets cwd itself).
  root: here,
  base: '/app/',
  plugins: [cspPlugin(command === 'serve'), react(), tailwindcss()],
  // frontend/agent has no local tsconfig (typecheck:web owns types via this
  // package's tsconfig.json), so pin the automatic JSX runtime for the agent
  // .tsx sources instead of relying on esbuild's tsconfig discovery.
  esbuild: {
    jsx: 'automatic',
    jsxImportSource: 'react'
  },
  build: {
    target: 'es2022',
    sourcemap: false,
    manifest: true,
    outDir: process.env.PSX_WEB_OUT_DIR || path.resolve(here, 'dist'),
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks(id: string): string | undefined {
          const normalized = id.split('\\').join('/');
          if (
            normalized.includes('node_modules/react/')
            || normalized.includes('node_modules/react-dom/')
            || normalized.includes('node_modules/scheduler/')
          ) {
            return 'react-vendor';
          }
          if (normalized.includes('/frontend/agent/src/core/markdown')) {
            return 'markdown';
          }
          return undefined;
        }
      }
    }
  },
  server: {
    // Debug-only HMR entry; the C# host validates loopback before use and
    // Release builds ignore PSX_WEB_DEV_SERVER entirely.
    port: 5199,
    strictPort: true,
    fs: {
      allow: [path.resolve(here, '..', '..')]
    }
  },
  resolve: {
    alias: {
      '@agent': agentSrc,
      // shadcn convention: components.json maps "@/..." onto the Agent source
      // tree (frontend/agent/src), matching tsconfig "paths".
      '@': agentSrc
    }
  }
}));
