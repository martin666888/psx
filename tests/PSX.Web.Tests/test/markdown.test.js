import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';
import { createAgentManager, installAgentRuntime } from './agentHarness.js';

describe('Agent markdown renderer', () => {
  let manager;

  beforeEach(() => {
    const runtime = installAgentRuntime();
    manager = createAgentManager(runtime.AgentThreadManager);
  });

  it('renders headings, lists, tables and fenced code', () => {
    const html = manager._renderMarkdown(`
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

  it('allows only safe link schemes', () => {
    const html = manager._renderMarkdown('[safe](https://example.com) [mail](mailto:test@example.com) [bad](javascript:alert(1))');

    assert.match(html, /href="https:\/\/example\.com"/);
    assert.match(html, /href="mailto:test@example\.com"/);
    assert.doesNotMatch(html, /javascript:/);
  });

  it('escapes scripts, event handlers and attributes while preserving the small safe tag list', () => {
    const html = manager._renderMarkdown('<script>alert(1)</script> <img src=x onerror=alert(1)> <details open><summary>Read</summary><b>Safe</b></details>');

    assert.doesNotMatch(html, /<script>/);
    assert.doesNotMatch(html, /<img/);
    assert.match(html, /&lt;img src=x onerror=alert\(1\)&gt;/);
    assert.match(html, /<summary>Read<\/summary>/);
    assert.match(html, /<b>Safe<\/b>/);
    assert.doesNotMatch(html, /<details open>/);
  });
});
