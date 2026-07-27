import { test } from 'vitest';
import assert from 'node:assert/strict';
import { appModule } from './agentHarness.js';

// The markdown renderer is a pure core/ module (no DOM). Assert its output
// directly instead of driving it through a manager instance.
const { renderMarkdown } = await appModule('core/markdown.js');

test('renders headings, lists, tables and fenced code', () => {
  const html = renderMarkdown(`
# Plan

- first
- second

| File | State |
| --- | --- |
| a.cs | changed |

\`\`\`csharp
Console.WriteLine("ok");
\`\`\`
`);

  assert.match(html, /<h1>Plan<\/h1>/);
  assert.match(html, /<ul><li>first<\/li><li>second<\/li><\/ul>/);
  assert.match(html, /<table>/);
  assert.match(html, /<pre><code>Console\.WriteLine\(&quot;ok&quot;\);<\/code><\/pre>/);
});

test('allows only safe link schemes', () => {
  const html = renderMarkdown('[safe](https://example.com) [mail](mailto:test@example.com) [bad](javascript:alert(1)) [port](https://example.com:8443)');

  assert.match(html, /href="https:\/\/example\.com\/"/);
  assert.match(html, /href="mailto:test@example\.com"/);
  assert.doesNotMatch(html, /javascript:/);
  assert.doesNotMatch(html, /8443/);
});

test('escapes scripts, event handlers and attributes while preserving the small safe tag list', () => {
  const html = renderMarkdown('<script>alert(1)</script> <img src=x onerror=alert(1)> <details open><summary>Read</summary><b>Safe</b></details>');

  assert.doesNotMatch(html, /<script>/);
  assert.doesNotMatch(html, /<img/);
  assert.match(html, /&lt;img src=x onerror=alert\(1\)&gt;/);
  assert.match(html, /<summary>Read<\/summary>/);
  assert.match(html, /<b>Safe<\/b>/);
  assert.doesNotMatch(html, /<details open>/);
});
