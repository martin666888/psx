import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'vitest';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../../..');

test('production WebView CSP is offline-only while development loopback stays mode-local', () => {
  const productionHtml = fs.readFileSync(path.join(repoRoot, 'wwwroot', 'app', 'index.html'), 'utf8');
  const sourceConfig = fs.readFileSync(
    path.join(repoRoot, 'frontend', 'webview', 'vite.config.ts'),
    'utf8'
  );
  const content = /http-equiv="Content-Security-Policy" content="([^"]+)"/.exec(productionHtml)?.[1];

  assert.ok(content, 'production index must carry a CSP meta element');
  assert.match(content, /default-src 'none'/);
  assert.match(content, /script-src 'self'/);
  assert.match(content, /style-src 'self' 'unsafe-inline'/);
  assert.match(content, /img-src 'self' data: blob: https:\/\/psx-attachments\.local/);
  assert.match(content, /object-src 'none'/);
  assert.match(content, /frame-src http:\/\/127\.0\.0\.1:\*/);
  assert.doesNotMatch(content, /https?:\/\/(?!psx-attachments\.local)(?!127\.0\.0\.1)/);
  assert.doesNotMatch(content, /wss?:\/\//);
  assert.match(sourceConfig, /command === 'serve'/);
  assert.match(sourceConfig, /ws:\/\/127\.0\.0\.1:5199/);
  assert.match(sourceConfig, /ws:\/\/localhost:5199/);
});
