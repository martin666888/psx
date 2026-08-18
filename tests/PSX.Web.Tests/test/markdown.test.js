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
const { isStreamingTextAnimationEnabled } = await appModule('markdown/streamingAnimation.js');

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

async function mountMarkdown(source, mode = 'static', configureRuntime) {
  globalThis.IS_REACT_ACT_ENVIRONMENT = false;
  installAgentRuntime();
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  configureRuntime?.();
  const host = document.createElement('div');
  host.className = 'agent-ui agent-message-body';
  document.body.appendChild(host);
  const root = createRoot(host);
  registerAgentCleanup(() => root.unmount());
  const render = async (nextSource, nextMode = mode) => {
    await act(async () => {
      root.render(createElement(MarkdownContent, {
        source: nextSource,
        mode: nextMode,
        surface: 'message'
      }));
      await Promise.resolve();
    });
    for (let attempt = 0; attempt < 30 && host.querySelector('[data-streamdown="loading"]'); attempt += 1) {
      await act(async () => new Promise((resolve) => setTimeout(resolve, 0)));
    }
  };
  await render(source, mode);
  return { host, render };
}

async function waitForSelector(host, selector, attempts = 30) {
  for (let attempt = 0; attempt < attempts && !host.querySelector(selector); attempt += 1) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 0)));
  }
  return host.querySelector(selector);
}

function installReducedMotion(value = false) {
  let matches = value;
  const listeners = new Set();
  let addCount = 0;
  let removeCount = 0;
  const media = '(prefers-reduced-motion: reduce)';
  const mql = {
    get matches() {
      return matches;
    },
    media,
    addEventListener(type, listener) {
      if (type === 'change') {
        addCount += 1;
        listeners.add(listener);
      }
    },
    removeEventListener(type, listener) {
      if (type === 'change') {
        removeCount += 1;
        listeners.delete(listener);
      }
    }
  };
  window.matchMedia = (query) => {
    assert.equal(query, media);
    return mql;
  };
  return {
    get addCount() {
      return addCount;
    },
    get removeCount() {
      return removeCount;
    },
    set(value) {
      if (matches === value) return;
      matches = value;
      for (const listener of listeners) listener({ matches, media });
    }
  };
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
  const { host, render } = await mountMarkdown('', 'streaming', () => {
    installReducedMotion(true);
  });
  for (let end = 1; end <= source.length; end += 9) {
    await render(source.slice(0, end), 'streaming');
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

test('short streaming text uses the fixed character fade without replaying its prefix', async () => {
  const { host, render } = await mountMarkdown('\u4e2d\u6587 Ab', 'streaming');
  await waitForSelector(host, '[data-sd-animate]');
  const initial = [...host.querySelectorAll('[data-sd-animate]')];
  assert.equal(initial.length, 4, 'continuous CJK and Latin text are split into non-whitespace characters');
  assert.ok(initial.every((node) => node.style.getPropertyValue('--sd-animation') === 'sd-fadeIn'));
  assert.ok(initial.every((node) => node.style.getPropertyValue('--sd-duration') === '120ms'));
  assert.ok(initial.every((node) => !node.style.getPropertyValue('--sd-delay')));

  await render('\u4e2d\u6587 AbC', 'streaming');
  await waitForSelector(host, '[data-sd-animate]');
  const updated = [...host.querySelectorAll('[data-sd-animate]')];
  assert.ok(updated.length > initial.length);
  const prefix = updated.slice(0, initial.length);
  assert.ok(prefix.every((node) => node.style.getPropertyValue('--sd-duration') === '0ms'));
  assert.ok(updated.slice(initial.length).some((node) => node.style.getPropertyValue('--sd-duration') === '120ms'));
});

test('streaming animation policy fixes the threshold, modes and large-source fallback', () => {
  assert.equal(isStreamingTextAnimationEnabled('streaming', 2048, false), true);
  assert.equal(isStreamingTextAnimationEnabled('streaming', 2049, false), false);
  assert.equal(isStreamingTextAnimationEnabled('static', 12, false), false);
  assert.equal(isStreamingTextAnimationEnabled('streaming', 12, true), false);
  assert.equal(isStreamingTextAnimationEnabled('streaming', 100 * 1024, false), false);
});

test('math plugin readiness disables streaming animation without rendering KaTeX twice', () => {
  assert.equal(isStreamingTextAnimationEnabled('streaming', 12, false, true), false);
});

test('reduced-motion uses one shared media subscription and follows system changes', async () => {
  installAgentRuntime();
  const motion = installReducedMotion(false);
  const {
    getReducedMotionSnapshot,
    subscribeReducedMotion
  } = await appModule('markdown/reducedMotion.js');
  let notifications = 0;
  const unsubscribeFirst = subscribeReducedMotion(() => { notifications += 1; });
  const unsubscribeSecond = subscribeReducedMotion(() => { notifications += 1; });
  assert.equal(getReducedMotionSnapshot(), false);
  assert.equal(motion.addCount, 1);
  motion.set(true);
  assert.equal(getReducedMotionSnapshot(), true);
  assert.equal(notifications, 2);
  unsubscribeFirst();
  assert.equal(motion.removeCount, 0);
  unsubscribeSecond();
  assert.equal(motion.removeCount, 1);
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

test('code fences show safe title metadata through the shared Streamdown code block', async () => {
  const host = await renderMarkdown(`
\`\`\`csharp title="Services/AutomationSupport.cs"
public static void Run() {}
\`\`\`
`);
  const label = host.querySelector('[data-psx-code-label]');
  assert.equal(label?.textContent, 'Services/AutomationSupport.cs');
  assert.ok(host.querySelector('[data-streamdown="code-block-copy-button"]'));
  assert.equal(host.querySelector('[data-streamdown="code-block-download-button"]'), null);

  const unsafeHost = await renderMarkdown(`
\`\`\`csharp filename="<img src=x>"
public static void Run() {}
\`\`\`
`);
  assert.equal(unsafeHost.querySelector('[data-psx-code-label]')?.textContent, '<img src=x>');
  assert.equal(unsafeHost.querySelector('img'), null);
});

test('markdown.css restores list markers and unifies block chrome', async () => {
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
  host.innerHTML = `
    <ul data-streamdown="unordered-list"><li>u</li></ul>
    <ol data-streamdown="ordered-list"><li>o</li></ol>
    <div data-streamdown="code-block">
      <div data-streamdown="code-block-header"></div>
      <div class="sticky-actions">
        <div data-streamdown="code-block-actions">
          <span data-psx-code-label>csharp</span>
          <button data-streamdown="code-block-copy-button"></button>
        </div>
      </div>
      <div data-streamdown="code-block-body"><pre><code><span>line</span></code></pre></div>
    </div>
    <div data-streamdown="table-wrapper">
      <div><div><button title="Copy table"></button></div></div>
      <div><table><tbody><tr><td>cell</td></tr></tbody></table></div>
    </div>
    <div data-streamdown="mermaid-block">
      <div><span>mermaid</span></div>
      <div class="sticky-actions">
        <div data-streamdown="mermaid-block-actions">
          <button data-streamdown="code-block-copy-button"></button>
          <button title="View fullscreen"></button>
        </div>
      </div>
      <div>diagram</div>
    </div>`;
  document.body.appendChild(host);
  const ul = host.querySelector('ul');
  const ol = host.querySelector('ol');
  assert.equal(window.getComputedStyle(ul).listStyleType, 'disc');
  assert.equal(window.getComputedStyle(ol).listStyleType, 'decimal');
  assert.equal(window.getComputedStyle(ul).paddingLeft, '22px');
  assert.equal(window.getComputedStyle(ol).marginLeft, '0px');
  assert.equal(window.getComputedStyle(host.querySelector('[data-streamdown="code-block-body"]')).paddingTop, '8px');
  assert.equal(window.getComputedStyle(host.querySelector('[data-streamdown="code-block"] pre')).paddingTop, '0px');

  const headers = [
    host.querySelector('[data-streamdown="code-block-header"]'),
    host.querySelector('[data-streamdown="table-wrapper"] > div:first-child'),
    host.querySelector('[data-streamdown="mermaid-block"] > div:first-child')
  ];
  const headerStyles = headers.map((header) => window.getComputedStyle(header));
  assert.equal(new Set(headerStyles.map((style) => style.height)).size, 1);
  assert.equal(new Set(headerStyles.map((style) => style.minHeight)).size, 1);
  assert.equal(
    window.getComputedStyle(host.querySelector('[data-streamdown="code-block"]'))
      .getPropertyValue('--psx-markdown-block-header-height').trim(),
    '28px'
  );

  const actionGroups = [
    host.querySelector('[data-streamdown="code-block-actions"]'),
    host.querySelector('[data-streamdown="table-wrapper"] > div:first-child'),
    host.querySelector('[data-streamdown="mermaid-block-actions"]')
  ];
  assert.deepEqual(
    actionGroups.map((group) => window.getComputedStyle(group).borderTopWidth),
    ['0px', '0px', '0px']
  );
  assert.deepEqual(
    actionGroups.map((group) => window.getComputedStyle(group).justifyContent),
    ['space-between', 'space-between', 'flex-end']
  );
  const buttons = [
    host.querySelector('[data-streamdown="code-block-actions"] button'),
    host.querySelector('[data-streamdown="table-wrapper"] > div:first-child > div > button'),
    host.querySelector('[data-streamdown="mermaid-block-actions"] button')
  ];
  const buttonStyles = buttons.map((button) => window.getComputedStyle(button));
  assert.equal(new Set(buttonStyles.map((style) => style.width)).size, 1);
  assert.equal(new Set(buttonStyles.map((style) => style.height)).size, 1);
  assert.deepEqual(buttonStyles.map((style) => style.borderTopWidth), ['0px', '0px', '0px']);

  const stickyActions = [...host.querySelectorAll('.sticky-actions')].map((action) =>
    window.getComputedStyle(action)
  );
  assert.equal(new Set(stickyActions.map((style) => style.top)).size, 1);
  assert.equal(new Set(stickyActions.map((style) => style.marginTop)).size, 1);
});

test('dark Agent theme uses Shiki dark token colors on code blocks', async () => {
  const { repositoryRoot } = await import('./agentHarness.js');
  const fs = await import('node:fs');
  const path = await import('node:path');
  installAgentRuntime();
  const markdownCss = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent', 'markdown.css'),
    'utf8'
  );
  const tailwindCss = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'tailwind.css'),
    'utf8'
  );
  assert.match(
    tailwindCss,
    /@custom-variant dark \(\&:where\(\.agent-ui-dark, \.agent-ui-dark \*\)\);/
  );
  assert.match(
    markdownCss,
    /\.agent-ui\.agent-ui-dark \[data-streamdown="code-block-body"\] \[style\*="--shiki-dark"\]/
  );
  assert.match(
    markdownCss,
    /color:\s*var\(--shiki-dark\)/
  );

  const style = document.createElement('style');
  style.textContent = markdownCss;
  document.head.appendChild(style);
  const host = document.createElement('div');
  host.className = 'agent-ui agent-ui-dark';
  host.innerHTML = `
    <div class="agent-message-body">
      <div data-streamdown="code-block-body">
        <span style="--sdm-c:#6f42c1;--shiki-dark:#b392f0">Connect</span>
      </div>
    </div>`;
  document.body.appendChild(host);
  const token = host.querySelector('span');
  assert.equal(window.getComputedStyle(token).color, 'var(--shiki-dark)');

  host.classList.remove('agent-ui-dark');
  assert.notEqual(window.getComputedStyle(token).color, 'var(--shiki-dark)');
});
