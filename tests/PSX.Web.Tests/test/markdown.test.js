import { afterEach, beforeEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import {
  appModule,
  installAgentRuntime,
  registerAgentCleanup
} from './agentHarness.js';

const {
  MarkdownContent,
  inspectMarkdownSource
} = await appModule('markdown/MarkdownContent.js');
const {
  createPsxMermaidOptions,
  markdownLimits,
  normalizePsxHref,
  normalizePsxImageSource
} = await appModule('markdown/security.js');
const { readMarkdownPluginLoadState } = await appModule('markdown/pluginLoader.js');

beforeEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
});

afterEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = false;
});

async function renderMarkdown(source, mode = 'static', configureRuntime) {
  globalThis.IS_REACT_ACT_ENVIRONMENT = false;
  installAgentRuntime();
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  if (configureRuntime) {
    configureRuntime();
  } else {
    globalThis.IntersectionObserver = class {
      observe() {}
      disconnect() {}
    };
  }
  const host = document.createElement('div');
  host.className = 'agent-ui agent-message-body';
  document.body.appendChild(host);
  const root = createRoot(host);
  registerAgentCleanup(() => root.unmount());
  await act(async () => {
    root.render(createElement(MarkdownContent, { source, mode, surface: 'message' }));
    await Promise.resolve();
  });
  for (let attempt = 0; attempt < 30 && host.querySelector('[data-streamdown="loading"]'); attempt += 1) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 0)));
  }
  return host;
}

test('renders semantic GFM lists with starts, nesting, loose items and tasks', async () => {
  const host = await renderMarkdown(`
3. third
4. fourth
   - nested

6. loose paragraph

   continuation

- [x] done
- [ ] pending
`);
  const ordered = host.querySelector('ol');
  assert.equal(ordered.start, 3);
  assert.deepEqual([...ordered.children].map((item) => item.tagName), ['LI', 'LI', 'LI']);
  assert.equal(ordered.querySelector('ul li').textContent.trim(), 'nested');
  assert.match(ordered.children[2].textContent, /loose paragraph\s+continuation/);
  const tasks = [...host.querySelectorAll('input[type="checkbox"]')];
  assert.equal(tasks.length, 2);
  assert.equal(tasks[0].checked, true);
  assert.ok(tasks.every((input) => input.disabled));
});

test('blank-separated ordered items remain one list instead of restarting at one', async () => {
  const host = await renderMarkdown('1. first\n\n1. second\n\n1. third');
  const lists = [...host.querySelectorAll('ol')];
  assert.equal(lists.length, 1);
  assert.equal(lists[0].children.length, 3);
  assert.equal(lists[0].start, 1);
});

test('completed offscreen code does not request Shiki until the 800px observer gate opens', async () => {
  let observerCallback = null;
  class TestIntersectionObserver {
    constructor(callback, options) {
      observerCallback = callback;
      assert.equal(options.rootMargin, '800px 0px');
    }
    observe() {}
    disconnect() {}
  }
  const host = await renderMarkdown('```ts\nconst value = 1;\n```', 'static', () => {
    globalThis.IntersectionObserver = TestIntersectionObserver;
  });
  assert.equal(readMarkdownPluginLoadState().code, false);
  assert.match(host.textContent, /const value = 1/);
  await act(async () => {
    observerCallback([{ isIntersecting: true }]);
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  assert.equal(readMarkdownPluginLoadState().code, true);
  delete globalThis.IntersectionObserver;
});

test('source inspection keeps Mermaid and Shiki requests independent', () => {
  assert.deepEqual(inspectMarkdownSource('```mermaid\ngraph TD\nA --> B\n```'), {
    code: false,
    math: false,
    mermaid: true
  });
  assert.deepEqual(inspectMarkdownSource('```mermaid\ngraph TD\n```\n\n```ts\nconst x = 1\n```'), {
    code: true,
    math: false,
    mermaid: true
  });
  assert.equal(inspectMarkdownSource('Width is $w_i$.').math, true);
  assert.equal(inspectMarkdownSource('Price is $5.').math, false);
  assert.equal(inspectMarkdownSource('Escaped \\$w_i\\$ stays literal.').math, false);
});

test('renders GFM tables, escaped pipes, strikethrough, footnotes and tilde fences', async () => {
  const host = await renderMarkdown(`
| Name | Value |
| :--- | ---: |
| pipe | a\\|b |

~~removed~~ and note[^1].

[^1]: Footnote text.

~~~text
plain code
~~~
`);
  assert.equal(host.querySelector('tbody td:nth-child(2)').textContent.trim(), 'a|b');
  assert.equal(host.querySelector('del').textContent, 'removed');
  assert.match(host.querySelector('[data-footnotes]').textContent, /Footnote text/);
  assert.match(host.querySelector('[data-streamdown="code-block"]').textContent, /plain code/);
});

test('locks CommonMark and CJK emphasis semantics after retiring the custom parser', async () => {
  const host = await renderMarkdown(
    'snake_case mode_transition mode__transition a**b 中文*强调*，以及 _合法斜体_。'
  );
  assert.match(host.textContent, /snake_case mode_transition mode__transition a\*\*b/);
  assert.deepEqual([...host.querySelectorAll('em')].map((node) => node.textContent), ['强调', '合法斜体']);
});

test('normalizes links with the PSX absolute URL policy', () => {
  assert.equal(normalizePsxHref('https://example.com/x'), 'https://example.com/x');
  assert.equal(normalizePsxHref('http://host/path'), 'http://host/path');
  assert.equal(normalizePsxHref('mailto:a@b.com'), 'mailto:a@b.com');
  assert.equal(normalizePsxHref('#user-content-fn-1'), '#user-content-fn-1');
  assert.equal(normalizePsxHref('javascript:evil'), '');
  assert.equal(normalizePsxHref('https://user:pass@example.com/'), '');
  assert.equal(normalizePsxHref('https://example.com:443/'), '');
  assert.equal(normalizePsxHref('https://127.0.0.1/path'), '');
  assert.equal(normalizePsxHref('/relative/path'), '');
});

test('renders blocked links as text and never leaves an unsafe href', async () => {
  const host = await renderMarkdown(
    '[safe](https://example.com) [bad](javascript:alert(1)) [port](https://example.com:8443)'
  );
  assert.equal(host.querySelectorAll('a').length, 1);
  assert.equal(host.querySelector('a').href, 'https://example.com/');
  assert.doesNotMatch(host.innerHTML, /javascript:|8443/);
  assert.match(host.textContent, /safe bad port/);
});

test('allows only attachment-host Markdown images and exposes no download control', async () => {
  assert.equal(
    normalizePsxImageSource('https://psx-attachments.local/thread/image.png'),
    'https://psx-attachments.local/thread/image.png'
  );
  assert.equal(normalizePsxImageSource('https://example.com/image.png'), '');
  assert.equal(normalizePsxImageSource('data:image/png;base64,AA=='), '');
  assert.equal(normalizePsxImageSource('file:///C:/secret.png'), '');

  const host = await renderMarkdown(
    '![allowed](https://psx-attachments.local/thread/image.png) ![remote](https://example.com/x.png)'
  );
  const images = [...host.querySelectorAll('img')];
  assert.equal(images.length, 1);
  assert.equal(images[0].getAttribute('src'), 'https://psx-attachments.local/thread/image.png');
  assert.match(host.textContent, /remote/);
  assert.equal(host.querySelector('[download]'), null);
});

test('keeps unsafe raw HTML inert while preserving the attribute-free safe tag list', async () => {
  const host = await renderMarkdown(
    '<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n<details open>Blocked attributes</details>\n\n<details><summary>Read</summary><b>Safe</b></details>'
  );
  assert.equal(host.querySelector('script'), null);
  assert.equal(host.querySelector('img'), null);
  assert.equal(host.querySelectorAll('details').length, 1, 'only the attribute-free details tag is parsed');
  assert.equal(host.querySelector('summary').textContent, 'Read');
  assert.equal(host.querySelector('b').textContent, 'Safe');
  assert.match(host.textContent, /<script>alert\(1\)<\/script>/);
  assert.match(host.textContent, /<img src=x onerror=alert\(1\)>/);
  assert.match(host.textContent, /<details open>Blocked attributes/);
});

test('streaming mode tolerates every prefix and converges to static DOM semantics', async () => {
  const source = '# Plan\n\n- one\n- two\n\n**bold** [link](https://example.com)\n\n```ts\nconst x = 1;\n```';
  for (let end = 1; end <= source.length; end += 9) {
    const host = await renderMarkdown(source.slice(0, end), 'streaming');
    assert.ok(host.querySelector('[data-markdown-mode="streaming"]'));
  }
  const streaming = await renderMarkdown(source, 'streaming');
  const streamingLists = streaming.querySelectorAll('li').length;
  const streamingStrong = streaming.querySelectorAll('strong').length;
  const streamingCode = streaming.querySelector('code')?.textContent;
  const streamingHeadings = streaming.querySelectorAll('h1').length;
  const staticHost = await renderMarkdown(source, 'static');
  assert.equal(streamingLists, staticHost.querySelectorAll('li').length);
  assert.equal(streamingStrong, staticHost.querySelectorAll('strong').length);
  assert.equal(streamingCode, staticHost.querySelector('code')?.textContent);
  assert.equal(streamingHeadings, staticHost.querySelectorAll('h1').length);
});

test('advanced source limits are fixed security contracts', () => {
  assert.deepEqual(markdownLimits, {
    mathSourceCharacters: 16 * 1024,
    mermaidSourceCharacters: 32 * 1024
  });
});

test('Mermaid security configuration is fixed outside document control', () => {
  const light = createPsxMermaidOptions(false);
  const dark = createPsxMermaidOptions(true);
  assert.equal(light.config.securityLevel, 'strict');
  assert.equal(light.config.startOnLoad, false);
  assert.equal(light.config.suppressErrorRendering, true);
  assert.equal(light.config.theme, 'neutral');
  assert.equal(dark.config.theme, 'dark');
});

test('KaTeX renders single-dollar inline and double-dollar block math while currency remains text', async () => {
  const host = await renderMarkdown(
    'Price is $5.\n\nWidth is $w_i$.\n\n$$\nx^2 + y^2 = z^2\n$$',
    'static',
    () => {
      globalThis.IntersectionObserver = class {
        constructor(callback) {
          this.callback = callback;
        }
        observe(target) {
          this.callback([{ target, isIntersecting: true }]);
        }
        disconnect() {}
      };
    }
  );
  for (let attempt = 0; attempt < 30 && !host.querySelector('.katex'); attempt += 1) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 10)));
  }
  assert.match(host.textContent, /Price is \$5\./);
  assert.equal(host.querySelectorAll('.katex').length, 2);
  assert.equal(host.querySelectorAll('.katex-display').length, 1);
  assert.equal(host.querySelectorAll('math').length, 2);
});

test('download controls stay absent for code, tables and Mermaid placeholders', async () => {
  const host = await renderMarkdown(`
| A |
| - |
| B |

\`\`\`js
const value = 1;
\`\`\`

\`\`\`mermaid
graph TD
  A --> B
\`\`\`
`);
  assert.equal(host.querySelector('[data-streamdown="code-block-download-button"]'), null);
  assert.equal(host.querySelector('[data-streamdown="table-download-button"]'), null);
  assert.equal(host.querySelector('[data-streamdown="mermaid-download-button"]'), null);
});

test('markdown.css restores list markers against Tailwind Preflight', async () => {
  const { repositoryRoot } = await import('./agentHarness.js');
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
  host.innerHTML = '<ul data-streamdown="unordered-list"><li>u</li></ul><ol data-streamdown="ordered-list"><li>o</li></ol>';
  document.body.appendChild(host);
  const ul = host.querySelector('ul');
  const ol = host.querySelector('ol');
  assert.equal(window.getComputedStyle(ul).listStyleType, 'disc');
  assert.equal(window.getComputedStyle(ol).listStyleType, 'decimal');
  assert.equal(window.getComputedStyle(ul).paddingLeft, '22px');
  assert.equal(window.getComputedStyle(ol).marginLeft, '0px');
});
