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
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';

const here = path.dirname(fileURLToPath(import.meta.url));
export const repoRoot = path.resolve(here, '..');
export const webviewRoot = path.join(repoRoot, 'frontend', 'webview');
export const committedAppDir = path.join(repoRoot, 'wwwroot', 'app');

const agentUiFontFamily = 'Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Segoe UI Emoji, sans-serif';
const mapleMonoFontFamily = 'PSX Maple Mono, Segoe UI Emoji, Microsoft YaHei UI, monospace';
const mapleMonoFiles = new Map([
  ['wwwroot/vendor/fonts/maple-mono/MapleMonoNormal-CN-Regular.ttf', 'E42D081EAECBDA6A043079EAAAF43EA20BD8805666BC06E1FE4DC663C462AD7F'],
  ['wwwroot/vendor/fonts/maple-mono/MapleMonoNormal-CN-SemiBold.ttf', 'F72D4475C7AC435C7C363E0CB35100A18E4A5BB14787F17F7BBC64823B904066'],
  ['licenses/maple-mono/LICENSE.txt', 'EB2D28D2E565A0757E3D64E34EBB452E75A0CAD87C0AB3FAF4E08BA7596DE902']
]);

const require = createRequire(import.meta.url);

function sha256File(file) {
  const hash = createHash('sha256');
  const descriptor = fs.openSync(file, 'r');
  const buffer = Buffer.allocUnsafe(1024 * 1024);
  try {
    let bytesRead;
    do {
      bytesRead = fs.readSync(descriptor, buffer, 0, buffer.length, null);
      if (bytesRead > 0) hash.update(buffer.subarray(0, bytesRead));
    } while (bytesRead > 0);
  } finally {
    fs.closeSync(descriptor);
  }
  return hash.digest('hex').toUpperCase();
}

export function validateBundledAgentFonts(label) {
  const problems = [];
  for (const [relative, expectedHash] of mapleMonoFiles) {
    const absolute = path.join(repoRoot, ...relative.split('/'));
    if (!fs.existsSync(absolute)) {
      problems.push(`missing bundled font resource: ${relative}`);
    } else if (sha256File(absolute) !== expectedHash) {
      problems.push(`bundled font resource hash mismatch: ${relative}`);
    }
  }

  const tokenCss = fs.readFileSync(path.join(webviewRoot, 'src', 'css', 'agent', 'tokens.css'), 'utf8');
  for (const expected of [
    'MapleMonoNormal-CN-Regular.ttf',
    'MapleMonoNormal-CN-SemiBold.ttf',
    'font-weight: 400',
    'font-weight: 600',
    'font-synthesis: style',
    '[data-agent-font-surface]'
  ]) {
    if (!tokenCss.includes(expected)) problems.push(`Agent font CSS is missing: ${expected}`);
  }

  for (const relative of ['src/css/workspace-chrome.css', 'src/css/runtime-diagnostics.css']) {
    const source = fs.readFileSync(path.join(webviewRoot, ...relative.split('/')), 'utf8');
    if (source.includes('var(--agent-font-ui)') || source.includes('var(--agent-font-mono)')) {
      problems.push(`${relative} still consumes an Agent font token`);
    }
    if (!source.includes('var(--workspace-font-ui)')) {
      problems.push(`${relative} does not consume the workspace font token`);
    }
  }

  const presetFiles = fs.readdirSync(path.join(repoRoot, 'theme-presets'))
    .filter((name) => name.endsWith('.ini'))
    .map((name) => `theme-presets/${name}`);
  for (const relative of ['psx.ini', ...presetFiles]) {
    const source = fs.readFileSync(path.join(repoRoot, ...relative.split('/')), 'utf8');
    if (!source.includes(`fontFamily=${agentUiFontFamily}`)
        || !source.includes(`monoFontFamily=${mapleMonoFontFamily}`)) {
      problems.push(`${relative} does not contain the complete proportional UI and bundled mono font stacks`);
    }
  }

  if (problems.length > 0) {
    throw new Error(`[${label}] bundled Agent font validation failed:\n  - ${problems.join('\n  - ')}`);
  }
}

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
  const emittedCss = assetFiles
    .filter((file) => file.endsWith('.css'))
    .map((file) => fs.readFileSync(path.join(outDir, file), 'utf8'))
    .join('\n');
  for (const expected of [
    '--default-font-family:var(--psx-font-sans)',
    '--default-mono-font-family:var(--psx-font-mono)',
    '.font-sans{font-family:var(--psx-font-sans)}',
    '.font-mono{font-family:var(--psx-font-mono)}'
  ]) {
    if (!emittedCss.includes(expected)) problems.push(`scoped font output is missing: ${expected}`);
  }
  for (const forbidden of [
    '--default-font-family:var(--agent-font-ui)',
    '--default-mono-font-family:var(--agent-font-mono)'
  ]) {
    if (emittedCss.includes(forbidden)) problems.push(`Agent font leaked into global Preflight: ${forbidden}`);
  }
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
