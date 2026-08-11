// Locked-Chromium performance contract for long Agent Markdown conversations.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const wwwroot = path.join(root, 'wwwroot');
const resultDir = path.join(root, 'TestResults', 'visual');
const reportPath = path.join(resultDir, 'markdown-performance.json');
const WORKSPACE_ID = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const TURN_COUNT = 500;
const STREAM_DELTA_COUNT = 240;
const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2'
};

function lockedChromiumExecutable() {
  const manifest = JSON.parse(fs.readFileSync(
    path.join(root, 'node_modules', 'playwright-core', 'browsers.json'),
    'utf8'
  ));
  const revision = manifest.browsers.find((browser) => browser.name === 'chromium')?.revision;
  if (!revision) throw new Error('Playwright Chromium descriptor is missing');
  return path.join(
    root,
    'TestResults',
    'playwright-browsers',
    `chromium-${revision}`,
    'chrome-win64',
    'chrome.exe'
  );
}

function percentile(values, ratio) {
  if (values.length === 0) return 0;
  const ordered = [...values].sort((left, right) => left - right);
  return ordered[Math.min(ordered.length - 1, Math.floor(ordered.length * ratio))];
}

function initialEvents() {
  return [
    {
      type: 'appearance_settings',
      settings: {
        agentFontSize: 14,
        themeColors: { background: '#ffffff', text: '#171717', border: '#e5e5e5' }
      }
    },
    {
      type: 'agent_workspace_created',
      workspaceId: WORKSPACE_ID,
      providerKey: 'claude-code',
      assistantName: 'Claude',
      providerDisplayName: 'Claude Code'
    },
    {
      type: 'workspace_catalog',
      revision: 1,
      maxColumns: 3,
      providers: [],
      workspaces: [{
        workspaceId: WORKSPACE_ID,
        kind: 'agent',
        title: 'Markdown performance',
        iconKey: 'agent',
        columnId: 'column-1',
        isActiveTab: true,
        canSplitRight: false,
        splitBlockedReason: '',
        canCollapse: false
      }]
    },
    {
      type: 'workspace_layout',
      revision: 1,
      focusedColumnId: 'column-1',
      columns: [{
        columnId: 'column-1',
        tabs: [{ workspaceId: WORKSPACE_ID, kind: 'agent' }],
        activeTabId: WORKSPACE_ID,
        ratio: 1
      }]
    },
    { type: 'workspace_activated', workspaceId: WORKSPACE_ID, kind: 'agent' },
    {
      type: 'runtime_status',
      workspaceId: WORKSPACE_ID,
      state: 'ready',
      message: 'Ready',
      canInstall: false,
      canCancel: false
    },
    {
      type: 'agent_state',
      workspaceId: WORKSPACE_ID,
      status: 'ready',
      cwd: 'D:/PSX-open-source',
      sessionId: 'markdown-performance',
      assistantName: 'Claude',
      busy: false,
      isDraft: false,
      supportsImage: false,
      contextUsedTokens: 100000,
      contextWindowTokens: 200000
    }
  ];
}

function conversationEvents() {
  const repeated = 'Long conversation evidence keeps the Markdown parser honest and measurable. ';
  const plain = repeated.repeat(24);
  const events = [];
  let markdownCharacters = 0;
  for (let index = 0; index < TURN_COUNT; index += 1) {
    let source = `### Turn ${index + 1}\n\n${plain}\n\n- stable list item\n- another item`;
    if (index < 20) source += `\n\n\`\`\`js\nconst turn = ${index};\n\`\`\``;
    else if (index < 40) source += `\n\n$$\nx_${index}^2 + y_${index}^2 = z_${index}^2\n$$`;
    else if (index < 45) source += `\n\n\`\`\`mermaid\ngraph TD\n  A${index} --> B${index}\n\`\`\``;
    markdownCharacters += source.length;
    events.push(
      { type: 'user_message', workspaceId: WORKSPACE_ID, text: `Question ${index + 1}` },
      { type: 'assistant_delta', workspaceId: WORKSPACE_ID, text: source },
      { type: 'run_finished', workspaceId: WORKSPACE_ID }
    );
  }
  return { events, markdownCharacters };
}

const server = http.createServer((request, response) => {
  const requestUrl = new URL(request.url, 'http://127.0.0.1');
  const relative = decodeURIComponent(requestUrl.pathname).replace(/^\/+/, '');
  const file = path.resolve(wwwroot, relative);
  if (!file.startsWith(wwwroot + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    response.writeHead(404).end('not found');
    return;
  }
  response.writeHead(200, {
    'content-type': MIME[path.extname(file)] || 'application/octet-stream',
    'cache-control': 'no-store'
  });
  response.end(fs.readFileSync(file));
});

fs.mkdirSync(resultDir, { recursive: true });
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;

try {
  const executablePath = lockedChromiumExecutable();
  if (!fs.existsSync(executablePath)) throw new Error(`locked Chromium is missing at ${executablePath}`);
  browser = await chromium.launch({ headless: true, executablePath });
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
    locale: 'zh-CN',
    reducedMotion: 'reduce'
  });
  const page = await context.newPage();
  const runtimeErrors = [];
  page.on('console', (message) => {
    if (message.type() === 'error') runtimeErrors.push(`console: ${message.text()}`);
  });
  page.on('pageerror', (error) => runtimeErrors.push(`page: ${String(error)}`));
  page.on('requestfailed', (request) =>
    runtimeErrors.push(`request: ${request.url()} (${request.failure()?.errorText || 'failed'})`)
  );
  await page.addInitScript(() => {
    const listeners = [];
    window.localStorage.setItem('psx.agent.historyDockOpen', '0');
    window.__psxEmit = (data) => listeners.forEach((listener) => listener({ data }));
    window.chrome = {
      webview: {
        postMessage() {},
        addEventListener(name, listener) {
          if (name === 'message') listeners.push(listener);
        }
      }
    };
  });
  await page.goto(`${origin}/app/index.html`, { waitUntil: 'load' });
  await page.evaluate((events) => events.forEach((event) => window.__psxEmit(event)), initialEvents());
  await page.waitForSelector('.agent-thread-scroll', { timeout: 10000 });

  const fixture = conversationEvents();
  const projectionStart = Date.now();
  await page.evaluate((events) => events.forEach((event) => window.__psxEmit(event)), fixture.events);
  await page.waitForFunction(
    (count) => document.querySelectorAll('.agent-turn').length === count,
    TURN_COUNT,
    { timeout: 30000 }
  );
  const remainingVisibilityBudget = Math.max(50, 1000 - (Date.now() - projectionStart));
  try {
    await page.waitForFunction(() => {
      const scroll = document.querySelector('.agent-thread-scroll');
      const last = document.querySelector('.agent-turn:last-child');
      const rect = last?.getBoundingClientRect();
      const scrollRect = scroll?.getBoundingClientRect();
      return !!rect && !!scrollRect && rect.bottom <= scrollRect.bottom + 1 && rect.bottom > scrollRect.top;
    }, undefined, { timeout: remainingVisibilityBudget });
  } catch {
    // The report below records the failed pinned-bottom condition.
  }
  const lastVisibleMs = Date.now() - projectionStart;
  const projection = await page.evaluate(() => {
    const scroll = document.querySelector('.agent-thread-scroll');
    const last = document.querySelector('.agent-turn:last-child');
    const rect = last?.getBoundingClientRect();
    const scrollRect = scroll?.getBoundingClientRect();
    return {
      turnCount: document.querySelectorAll('.agent-turn').length,
      lastVisible: !!rect && !!scrollRect && rect.bottom <= scrollRect.bottom + 1 && rect.bottom > scrollRect.top,
      heavyResources: performance.getEntriesByType('resource')
        .map((entry) => entry.name)
        .filter((name) => /markdown-(?:code|math|mermaid|katex)-|shiki-worker/.test(name))
    };
  });

  const streaming = await page.evaluate(async ({ workspaceId, deltaCount }) => {
    const scroll = document.querySelector('.agent-thread-scroll');
    scroll.scrollTop = scroll.scrollHeight;
    let mutationCommits = 0;
    const observer = new window.MutationObserver(() => { mutationCommits += 1; });
    observer.observe(document.querySelector('.agent-thread'), { childList: true, subtree: true, characterData: true });
    window.__psxEmit({ type: 'user_message', workspaceId, text: 'Stream a 100 KiB reply.' });
    const chunk = 'streaming-markdown '.repeat(24);
    const start = performance.now();
    for (let index = 0; index < deltaCount; index += 1) {
      window.__psxEmit({ type: 'assistant_delta', workspaceId, text: chunk });
      await new Promise((resolve) => setTimeout(resolve, 1));
    }
    window.__psxEmit({ type: 'run_finished', workspaceId });
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    observer.disconnect();
    return { durationMs: performance.now() - start, mutationCommits };
  }, { workspaceId: WORKSPACE_ID, deltaCount: STREAM_DELTA_COUNT });

  const frameIntervals = await page.evaluate(async () => {
    const scroll = document.querySelector('.agent-thread-scroll');
    const intervals = [];
    let previous = performance.now();
    for (let frame = 0; frame < 120; frame += 1) {
      await new Promise((resolve) => requestAnimationFrame((now) => {
        intervals.push(now - previous);
        previous = now;
        scroll.scrollTop = Math.max(0, scroll.scrollTop - 3);
        resolve();
      }));
    }
    return intervals.slice(2);
  });

  const cdp = await context.newCDPSession(page);
  await cdp.send('Performance.enable');
  const metrics = await cdp.send('Performance.getMetrics');
  const jsHeapUsed = metrics.metrics.find((metric) => metric.name === 'JSHeapUsedSize')?.value ?? 0;
  const p95FrameMs = percentile(frameIntervals, 0.95);
  const droppedFrameRatio = frameIntervals.filter((interval) => interval > 32).length / frameIntervals.length;
  const report = {
    schemaVersion: 1,
    scenario: {
      turns: TURN_COUNT,
      markdownCharacters: fixture.markdownCharacters,
      codeBlocks: 20,
      formulas: 20,
      mermaidBlocks: 5,
      streamDeltas: STREAM_DELTA_COUNT
    },
    results: {
      lastVisibleMs,
      lastVisible: projection.lastVisible,
      projectedTurns: projection.turnCount,
      offscreenHeavyResourceCount: projection.heavyResources.length,
      streamingDurationMs: streaming.durationMs,
      streamingMutationCommits: streaming.mutationCommits,
      p95FrameMs,
      droppedFrameRatio,
      jsHeapUsedBytes: jsHeapUsed
    },
    thresholds: {
      lastVisibleMs: 1000,
      p95FrameMs: 32,
      droppedFrameRatio: 0.05,
      offscreenHeavyResourceCount: 0
    },
    runtimeErrors
  };
  fs.writeFileSync(reportPath, JSON.stringify(report, null, 2));

  const failures = [];
  if (lastVisibleMs > report.thresholds.lastVisibleMs || !projection.lastVisible) {
    failures.push(`last turn visible in ${lastVisibleMs} ms (limit 1000 ms)`);
  }
  if (projection.heavyResources.length !== 0) {
    failures.push(`offscreen heavy resources loaded: ${projection.heavyResources.join(', ')}`);
  }
  if (p95FrameMs > report.thresholds.p95FrameMs) failures.push(`P95 frame ${p95FrameMs.toFixed(1)} ms`);
  if (droppedFrameRatio >= report.thresholds.droppedFrameRatio) {
    failures.push(`dropped-frame ratio ${(droppedFrameRatio * 100).toFixed(2)}%`);
  }
  if (runtimeErrors.length) failures.push(runtimeErrors.join(' | '));
  await context.close();
  if (failures.length) throw new Error(`${failures.join('; ')}. Diagnostic: ${reportPath}`);
  console.log(
    `[markdown performance] ${TURN_COUNT} turns/${fixture.markdownCharacters} chars: `
    + `last ${lastVisibleMs} ms, P95 ${p95FrameMs.toFixed(1)} ms, `
    + `dropped ${(droppedFrameRatio * 100).toFixed(2)}%, heap ${(jsHeapUsed / 1024 / 1024).toFixed(1)} MiB.`
  );
} finally {
  await browser?.close();
  await new Promise((resolve) => server.close(resolve));
}
