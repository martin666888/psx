// web-build-lib.mjs — shared helpers for build:web / verify:web.
//
// Contract (campaign plan CP1b):
// - the Vite output is validated for: manifest presence, the main entry, the
//   expected dynamic chunks (agent entry, react-vendor, Markdown core and
//   optional renderers), forbidden
//   files (node_modules/.ts/.map/dev-server/Component Lab/fixtures), and a
//   single bundled React instance;
// - build:web atomically replaces wwwroot/app only after validation;
// - verify:web never mutates the committed output.

import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';

const here = path.dirname(fileURLToPath(import.meta.url));
export const repoRoot = path.resolve(here, '..');
export const webviewRoot = path.join(repoRoot, 'frontend', 'webview');
export const committedAppDir = path.join(repoRoot, 'wwwroot', 'app');

const require = createRequire(import.meta.url);

export function runViteBuild(outDir, label) {
  fs.rmSync(outDir, { recursive: true, force: true });
  // vite's package exports hide bin/vite.js; resolve the executable through
  // the package.json "bin" field instead.
  const vitePackageDir = path.dirname(require.resolve('vite/package.json'));
  const viteBin = path.join(vitePackageDir, JSON.parse(fs.readFileSync(path.join(vitePackageDir, 'package.json'), 'utf8')).bin.vite);
  execFileSync(process.execPath, [viteBin, 'build', '--config', path.join(webviewRoot, 'vite.config.ts')], {
    cwd: webviewRoot,
    stdio: 'inherit',
    env: { ...process.env, PSX_WEB_OUT_DIR: outDir }
  });
  if (!fs.existsSync(outDir)) {
    throw new Error(`[${label}] vite build produced no output at ${outDir}`);
  }
}

export function collectFiles(dir) {
  const out = [];
  const walk = (rel) => {
    const abs = path.join(dir, rel);
    for (const entry of fs.readdirSync(abs, { withFileTypes: true })) {
      const childRel = rel ? path.join(rel, entry.name) : entry.name;
      if (entry.isDirectory()) walk(childRel);
      else out.push(childRel.split(path.sep).join('/'));
    }
  };
  if (fs.existsSync(dir)) walk('');
  return out.sort();
}

export function validateOutput(outDir, label) {
  const files = collectFiles(outDir);
  const problems = [];

  // 1) entry + manifest
  if (!files.includes('index.html')) problems.push('missing index.html');
  const manifestPath = '.vite/manifest.json';
  if (!files.includes(manifestPath)) problems.push('missing .vite/manifest.json');

  let manifest = {};
  if (files.includes(manifestPath)) {
    manifest = JSON.parse(fs.readFileSync(path.join(outDir, manifestPath), 'utf8'));
    const entries = Object.values(manifest);
    if (!entries.some((entry) => entry.isEntry)) problems.push('manifest has no entry chunk');
    const agentChunk = Object.entries(manifest).find(([key, entry]) =>
      key.endsWith('agent/src/entry.ts')
      || (entry.isDynamicEntry && /(^|\/)entry-[-\w]+\.js$/.test(entry.file)));
    if (!agentChunk) problems.push('manifest is missing the dynamic agent entry chunk');
    else if (!agentChunk[1].isDynamicEntry) problems.push('agent entry chunk is not a dynamic entry');
    for (const [key, entry] of Object.entries(manifest)) {
      if (!fs.existsSync(path.join(outDir, entry.file))) {
        problems.push(`manifest references a missing file: ${key} -> ${entry.file}`);
      }
    }

    const findChunk = (name) => Object.entries(manifest).find(([, entry]) =>
      entry.file.startsWith(`assets/${name}-`) && entry.file.endsWith('.js'));
    const staticClosure = (startKey) => {
      const visited = new Set();
      const visit = (key) => {
        if (!key || visited.has(key)) return;
        visited.add(key);
        for (const dependency of manifest[key]?.imports || []) visit(dependency);
      };
      visit(startKey);
      return visited;
    };
    const markdownCore = findChunk('markdown-core');
    if (!markdownCore) {
      problems.push('manifest is missing markdown-core');
    } else {
      const coreGraphFiles = [...staticClosure(markdownCore[0])]
        .map((key) => manifest[key]?.file || '');
      for (const heavy of ['markdown-code', 'markdown-math', 'markdown-mermaid']) {
        const chunk = findChunk(heavy);
        if (!chunk) {
          problems.push(`manifest is missing ${heavy}`);
        } else if (!chunk[1].isDynamicEntry) {
          problems.push(`${heavy} is not a dynamic entry`);
        }
        if (coreGraphFiles.some((file) => file.startsWith(`assets/${heavy}-`))) {
          problems.push(`${heavy} leaked into the static markdown-core import graph`);
        }
      }
      if (coreGraphFiles.some((file) => file.startsWith('assets/markdown-katex-'))) {
        problems.push('markdown-katex leaked into the static markdown-core import graph');
      }
    }

    const mainEntry = Object.entries(manifest).find(([key, entry]) =>
      key.endsWith('index.html') && entry.isEntry);
    const workerEntry = Object.entries(manifest).find(([, entry]) =>
      entry.file === 'assets/shiki-worker.js' && entry.isEntry);
    if (!workerEntry) {
      problems.push('manifest is missing the Shiki worker entry');
    } else {
      const workerFiles = [...staticClosure(workerEntry[0])]
        .map((key) => manifest[key]?.file || '');
      if (workerFiles.some((file) => /react-vendor|markdown-(?:core|code|math|mermaid)/.test(file))) {
        problems.push('Shiki worker statically imports React or a Markdown renderer chunk');
      }
    }
    if (mainEntry) {
      const mainFiles = [...staticClosure(mainEntry[0])]
        .map((key) => manifest[key]?.file || '');
      if (mainFiles.includes('assets/shiki-worker.js')) {
        problems.push('Shiki worker leaked into the application initial import graph');
      }
      if (mainFiles.some((file) => file.startsWith('assets/markdown-core-'))) {
        problems.push('Markdown core leaked into the terminal-only initial import graph');
      }
    }
    if (agentChunk) {
      const agentFiles = [...staticClosure(agentChunk[0])]
        .map((key) => manifest[key]?.file || '');
      if (agentFiles.some((file) => file.startsWith('assets/markdown-core-'))) {
        problems.push('Markdown core leaked into the empty Agent shell import graph');
      }
    }
  }

  const assetFiles = files.filter((file) => file.startsWith('assets/'));
  if (!assetFiles.includes('assets/shiki-worker.js')) {
    problems.push('missing fixed local Shiki worker entry');
  }
  if (!assetFiles.some((file) => /react-vendor-[-\w]+\.js$/.test(file))) {
    problems.push('missing react-vendor chunk');
  }
  for (const chunk of ['markdown-core', 'markdown-code', 'markdown-math', 'markdown-mermaid']) {
    if (!assetFiles.some((file) => new RegExp(`${chunk}-[-\\w]+\\.js$`).test(file))) {
      problems.push(`missing ${chunk} chunk`);
    }
  }
  if (!assetFiles.some((file) => /markdown-katex-[-\w]+\.js$/.test(file))) {
    problems.push('missing markdown-katex shared chunk');
  }
  const katexFonts = assetFiles.filter((file) => /\/KaTeX_[^/]+\.(?:woff2?|ttf)$/.test(file));
  if (katexFonts.length === 0) problems.push('missing local KaTeX font assets');
  const legacyKatexFonts = katexFonts.filter((file) => !file.endsWith('.woff2'));
  if (legacyKatexFonts.length > 0) {
    problems.push(`KaTeX emitted redundant non-WOFF2 fonts: ${legacyKatexFonts.join(', ')}`);
  }
  if (assetFiles.length > 200) {
    problems.push(`asset count ${assetFiles.length} exceeds the Markdown packaging ceiling of 200`);
  }

  // 2) forbidden files
  for (const file of files) {
    if (file === '.vite/manifest.json') continue;
    if (/(^|\/)node_modules\//.test(file)) problems.push(`forbidden node_modules path: ${file}`);
    if (file.endsWith('.ts') || file.endsWith('.tsx')) problems.push(`forbidden TypeScript source: ${file}`);
    if (file.endsWith('.map')) problems.push(`forbidden source map: ${file}`);
    if (/component-lab|componentlab/i.test(file)) problems.push(`forbidden Component Lab artifact: ${file}`);
    // The Debug-only Component Lab page (frontend/webview/lab.html + src/lab)
    // must never be part of the production build input.
    if (/(^|\/)lab\.html$/.test(file) || /(^|\/)lab-[-\w]+\.js$/.test(file)) {
      problems.push(`forbidden Component Lab artifact: ${file}`);
    }
    if (/fixture/i.test(file)) problems.push(`forbidden fixture artifact: ${file}`);
  }
  const html = files.includes('index.html')
    ? fs.readFileSync(path.join(outDir, 'index.html'), 'utf8')
    : '';
  if (html.includes('/@vite/client')) problems.push('index.html references the dev server client');
  if (/(?:'|")(?:unsafe-eval|wasm-unsafe-eval)(?:'|")/.test(html)) {
    problems.push('production CSP enables eval for Markdown rendering');
  }

  // 3) single React instance: the minified React runtime banner must appear in
  // exactly one emitted chunk (react + react-dom share the react-vendor chunk).
  const reactMarker = 'Minified React error';
  const chunksWithReact = assetFiles.filter((file) =>
    file.endsWith('.js') && fs.readFileSync(path.join(outDir, file), 'utf8').includes(reactMarker));
  if (chunksWithReact.length !== 1) {
    problems.push(`expected exactly one chunk with the React runtime, found ${chunksWithReact.length}: ${chunksWithReact.join(', ')}`);
  }

  if (problems.length > 0) {
    throw new Error(`[${label}] output validation failed:\n  - ${problems.join('\n  - ')}`);
  }
  return files;
}

export function compareDirectories(actualDir, expectedDir) {
  const actual = collectFiles(actualDir);
  const expected = collectFiles(expectedDir);
  const differences = [];
  for (const file of expected) {
    if (!actual.includes(file)) differences.push(`missing from committed output: ${file}`);
  }
  for (const file of actual) {
    if (!expected.includes(file)) differences.push(`stale committed file: ${file}`);
  }
  for (const file of expected) {
    if (!actual.includes(file)) continue;
    const a = fs.readFileSync(path.join(actualDir, file));
    const b = fs.readFileSync(path.join(expectedDir, file));
    if (!a.equals(b)) differences.push(`byte difference: ${file}`);
  }
  return differences;
}
