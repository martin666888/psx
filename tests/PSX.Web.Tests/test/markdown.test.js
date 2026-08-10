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

test('emphasis delimiters do not pair inside identifiers', () => {
  const cross = renderMarkdown('mode_transition then switch_mode');
  assert.doesNotMatch(cross, /<em>/);
  assert.match(cross, /mode_transition then switch_mode/);

  assert.doesNotMatch(renderMarkdown('snake_case'), /<em>/);
  assert.doesNotMatch(renderMarkdown('中文_标识符'), /<em>/);
  assert.doesNotMatch(renderMarkdown('path/to_file_name.ts --flag_value'), /<em>/);

  assert.match(renderMarkdown('_合法斜体_'), /<em>合法斜体<\/em>/);
  assert.match(renderMarkdown('*合法斜体*'), /<em>合法斜体<\/em>/);
  assert.match(renderMarkdown('__粗体__'), /<strong>粗体<\/strong>/);
  assert.match(renderMarkdown('**粗体**'), /<strong>粗体<\/strong>/);

  const boldCross = renderMarkdown('mode__transition then switch__mode');
  assert.doesNotMatch(boldCross, /<strong>/);
  assert.match(boldCross, /mode__transition then switch__mode/);
  assert.doesNotMatch(renderMarkdown('a**b then c**d'), /<strong>/);

  const code = renderMarkdown('use `mode_transition` and `*stars*`');
  assert.match(code, /<code>mode_transition<\/code>/);
  assert.match(code, /<code>\*stars\*<\/code>/);
  assert.doesNotMatch(code, /<em>/);
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

test('memoized renderer is byte-identical to renderMarkdown (incl. streaming prefixes)', async () => {
  const { renderMarkdownMemoized } = await appModule('core/markdown.js');
  const documents = [
    '',
    'plain paragraph',
    '# Title\n\nintro text\n\n## Sub\n\nmore',
    '- a\n- b\n- c\n\n1. one\n2. two\n\n- mixed after ol',
    '> quote line\n\n---\n\n***',
    '| File | State |\n| --- | --- |\n| a.cs | changed |\n| b.cs | added |',
    'para\n\n| H |\n| --- |\n| x |\n\ntail',
    'before\n\n```csharp\nvar x = 1;\n// comment\n```\n\nafter',
    '```\nno lang fence\n```',
    'text with `inline code` and **bold** and [link](https://example.com)',
    'paragraph with a > not-a-quote\n\n> real quote',
    '1) alt ordered\n2) style',
    'line one\nline two same paragraph',
    'ends mid-word strea'
  ];
  for (const doc of documents) {
    assert.equal(renderMarkdownMemoized(doc), renderMarkdown(doc), `mismatch for: ${JSON.stringify(doc.slice(0, 40))}`);
  }
  // Streaming simulation: every prefix of a growing document must also match.
  const stream = '# Plan\n\nintro\n\n- a\n- b\n\n```ts\nconst x = 1;\n```\n\ntail paragraph grows';
  for (let end = 1; end <= stream.length; end += 7) {
    const prefix = stream.slice(0, end);
    assert.equal(renderMarkdownMemoized(prefix), renderMarkdown(prefix), `mismatch at prefix ${end}`);
  }
  // Cache reuse must never change results on repeat calls.
  for (const doc of documents) {
    assert.equal(renderMarkdownMemoized(doc), renderMarkdown(doc), `repeat mismatch: ${JSON.stringify(doc.slice(0, 40))}`);
  }
});

// Tailwind Preflight clears list markers globally; markdown.css must restore
// them on .agent-message-body or assistant lists look like indented plain text.
test('markdown.css restores list markers against Preflight list-style:none', async () => {
  const { installAgentRuntime, repositoryRoot } = await import('./agentHarness.js');
  const fs = await import('node:fs');
  const path = await import('node:path');
  installAgentRuntime();
  const css = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent', 'markdown.css'),
    'utf8'
  );
  const style = document.createElement('style');
  style.textContent = 'ol,ul,menu{list-style:none}' + css;
  document.head.appendChild(style);
  const host = document.createElement('div');
  host.className = 'agent-message-body';
  host.innerHTML = '<ul><li>u</li></ul><ol><li>o</li></ol>';
  document.body.appendChild(host);
  const ul = host.querySelector('ul');
  const ol = host.querySelector('ol');
  assert.equal(window.getComputedStyle(ul).listStyleType, 'disc');
  assert.equal(window.getComputedStyle(ol).listStyleType, 'decimal');
  // Indent lives on padding so outside markers stay inside overflow-hidden parents.
  assert.equal(window.getComputedStyle(ul).paddingLeft, '22px');
  assert.equal(window.getComputedStyle(ol).marginLeft, '0px');
});
