// screenshot-baseline.mjs — CP5 visual baseline: 3 widths x 2 themes x 4
// scenes against the packaged Vite output, plus an axe accessibility pass on
// a representative page. Uses the system Edge channel (Chromium) so no
// browser download is required.
//
// Usage: node tools/screenshot-baseline.mjs [wwwroot-dir] [out-dir]
//   defaults: wwwroot, TestResults/screenshots
/* global HTMLTextAreaElement, Event */
// (the page.evaluate callbacks below run in the browser, not in Node)

import { chromium } from 'playwright';
import AxeBuilder from '@axe-core/playwright';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const wwwroot = path.resolve(root, process.argv[2] || 'wwwroot');
const outDir = path.resolve(root, process.argv[3] || 'TestResults/screenshots');
fs.mkdirSync(outDir, { recursive: true });

const WIDTHS = [1000, 1440, 1800];
const HEIGHT = 900;

// --- theme payloads from the shipped presets --------------------------------

function parseIni(file) {
  const sections = {};
  let current = '';
  for (const raw of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith(';')) continue;
    const section = /^\[(.+)\]$/.exec(line);
    if (section) {
      current = section[1];
      sections[current] = {};
      continue;
    }
    const eq = line.indexOf('=');
    if (eq > 0 && current) sections[current][line.slice(0, eq).trim()] = line.slice(eq + 1).trim();
  }
  return sections;
}

function appearanceFromPreset(file) {
  const ini = parseIni(file);
  return {
    agentFontSize: Number(ini.agent?.fontSize) || 14,
    agentFontFamily: ini.agent?.fontFamily || '',
    agentMonoFontFamily: ini.agent?.monoFontFamily || '',
    themeColors: ini.theme || {},
    agentThemeColors: ini.agentTheme || {},
    terminalColors: ini.terminalColors || {}
  };
}

const THEMES = {
  'vercel-dark': appearanceFromPreset(path.join(root, 'theme-presets', 'vercel-neutral-dark.ini')),
  'vercel-light': appearanceFromPreset(path.join(root, 'theme-presets', 'vercel-neutral-light.ini'))
};

// --- static server (same mapping as tools/smoke-server.mjs) -----------------

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2'
};

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const file = path.join(wwwroot, decodeURIComponent(url.pathname));
  if (!file.startsWith(wwwroot) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.writeHead(404).end('not found');
    return;
  }
  res.writeHead(200, { 'content-type': MIME[path.extname(file)] || 'application/octet-stream' });
  res.end(fs.readFileSync(file));
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;

// --- host-bridge stub + scenes ----------------------------------------------

const WS = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';

const INIT_STUB = `(() => {
  const listeners = [];
  window.__psxEmit = (data) => listeners.forEach((l) => l({ data }));
  window.chrome = {
    webview: {
      postMessage(payload) {
        let message = payload;
        if (typeof message === 'string') { try { message = JSON.parse(message); } catch { return; } }
        if (message && message.type === 'agent_command' && message.command === 'history') {
          setTimeout(() => window.__psxEmit({
            type: 'agent_threads', workspaceId: message.workspaceId,
            threads: [
              { threadId: 'h1', title: 'Ship the release notes', cwd: 'D:/proj', updatedAt: '2026-07-20 10:00:00Z', sessionId: '', provider: 'claude-code', providerKey: 'claude-code' },
              { threadId: 'h2', title: 'Refactor the storage layer', cwd: 'D:/proj', updatedAt: '2026-07-19 10:00:00Z', sessionId: '', provider: 'claude-code', providerKey: 'claude-code' },
              { threadId: 'h3', title: 'Fix flaky terminal test', cwd: 'D:/other', updatedAt: '2026-07-18 10:00:00Z', sessionId: '', provider: 'gemini', providerKey: 'gemini' }
            ]
          }), 0);
        }
      },
      addEventListener(name, listener) { if (name === 'message') listeners.push(listener); }
    }
  };
})();`;

function baseEvents(theme) {
  return [
    { type: 'appearance_settings', settings: theme },
    { type: 'agent_workspace_created', workspaceId: WS },
    { type: 'workspace_activated', workspaceId: WS, kind: 'agent' },
    {
      type: 'agent_state', workspaceId: WS, status: 'ready', cwd: 'D:/project',
      sessionId: 'baseline', busy: false, isDraft: false, supportsImage: true,
      contextUsedTokens: 4200, contextWindowTokens: 200000
    }
  ];
}

const SCENES = {
  'streaming-thread': (theme) => [
    ...baseEvents(theme),
    { type: 'user_message', workspaceId: WS, text: 'Walk me through the release checklist and start the verification run.' },
    { type: 'assistant_delta', workspaceId: WS, text: 'Here is the plan for the verification run.\n\n1. Rebuild the web bundle\n2. Re-run the fast suite\n3. Capture the release evidence\n\nStarting with the rebuild now — the bundle hash must match the committed output byte for byte.' },
    { type: 'run_finished', workspaceId: WS },
    { type: 'user_message', workspaceId: WS, text: 'Good. Keep going and summarize when done.' },
    { type: 'thinking_started', workspaceId: WS },
    { type: 'thinking_delta', workspaceId: WS, text: 'Comparing the fresh build against the committed output and collecting the gate results before writing the summary.' }
  ],
  'tool-calls': (theme) => [
    ...baseEvents(theme),
    { type: 'user_message', workspaceId: WS, text: 'Run the fast suite and show me the failures.' },
    { type: 'thinking_started', workspaceId: WS },
    { type: 'thinking_delta', workspaceId: WS, text: 'The fast suite covers the web tests and the dotnet unit tests.' },
    { type: 'thinking_finished', workspaceId: WS },
    { type: 'tool_started', workspaceId: WS, runId: 'r1', toolCallId: 't1', name: 'Bash', summary: 'npm run test:web', input: 'npm run test:web' },
    { type: 'tool_delta', workspaceId: WS, toolCallId: 't1', text: '\nTest Files  29 passed (29)\nTests  187 passed (187)' },
    { type: 'tool_finished', workspaceId: WS, toolCallId: 't1', status: 'completed' },
    { type: 'tool_started', workspaceId: WS, runId: 'r1', toolCallId: 't2', name: 'Bash', summary: 'dotnet test', input: 'dotnet test tests/PSX.Tests' },
    { type: 'tool_delta', workspaceId: WS, toolCallId: 't2', text: '\nPassed!  - 212 tests' },
    { type: 'tool_finished', workspaceId: WS, toolCallId: 't2', status: 'completed' },
    { type: 'plan_update', workspaceId: WS, runId: 'r1', entries: [
      { content: 'Run web test suite', status: 'completed' },
      { content: 'Run dotnet unit tests', status: 'completed' },
      { content: 'Summarize results', status: 'in_progress' }
    ] }
  ],
  'permission-elicitation': (theme) => [
    ...baseEvents(theme),
    { type: 'user_message', workspaceId: WS, text: 'Clean up the stale build artifacts.' },
    { type: 'permission_request', workspaceId: WS, requestId: 'p1', title: 'Run: Remove-Item bin/stale -Recurse', text: 'Remove-Item bin/stale -Recurse', options: [
      { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
      { optionId: 'always', name: 'Always allow', kind: 'allow_always' },
      { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
    ] },
    { type: 'elicitation_request', workspaceId: WS, requestId: 'e1', message: 'Which artifact groups should be removed?', schema: {
      properties: {
        groups: { type: 'array', title: 'Artifact groups', items: { enum: ['bin', 'obj', 'TestResults'] }, default: ['bin'] },
        reason: { type: 'string', title: 'Reason' }
      },
      required: ['groups', 'reason']
    } }
  ],
  'history-composer': (theme) => [
    ...baseEvents(theme),
    { type: 'user_message', workspaceId: WS, text: 'Show me the saved threads.' },
    { type: 'assistant_delta', workspaceId: WS, text: 'The History dock on the left lists every saved Agent thread grouped by workspace folder.' },
    { type: 'run_finished', workspaceId: WS }
  ]
};

// history-composer opens the dock and pre-fills the composer for the shot.
async function stageHistoryComposer(page) {
  await page.evaluate(() => {
    // First-run default is open on wide viewports; only narrow layouts need
    // the toolbar toggle to re-open the responsively collapsed dock.
    const dock = document.querySelector('.agent-history-dock');
    if (dock?.hidden) {
      const panel = document.querySelector('[data-workspace-id="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"]');
      panel?.querySelector('[data-role="history-toggle"]')?.click();
    }
  });
  await page.waitForSelector('.agent-history-item', { timeout: 5000 });
  await page.evaluate(() => {
    const input = document.querySelector('[data-role="input"]');
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
    setter.call(input, '/');
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await page.waitForSelector('[data-role="command-menu"]:not([hidden])', { timeout: 5000 });
}

const READY = {
  'streaming-thread': '.agent-thinking-block',
  'tool-calls': '.agent-tool-card[data-tool-id="t2"]',
  'permission-elicitation': '.agent-decision-elicitation',
  'history-composer': '.agent-message-assistant'
};

// --- run ---------------------------------------------------------------------

const browser = await chromium.launch({ channel: 'msedge' });
const failures = [];
let shots = 0;

for (const [themeName, theme] of Object.entries(THEMES)) {
  for (const [sceneName, build] of Object.entries(SCENES)) {
    for (const width of WIDTHS) {
      const context = await browser.newContext({ viewport: { width, height: HEIGHT } });
      const page = await context.newPage();
      const consoleErrors = [];
      page.on('console', (msg) => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });
      page.on('pageerror', (error) => consoleErrors.push(String(error)));
      await page.addInitScript(INIT_STUB);
      await page.goto(`${origin}/app/index.html`, { waitUntil: 'load' });
      await page.evaluate((events) => { for (const event of events) window.__psxEmit(event); }, build(theme));
      try {
        await page.waitForSelector(READY[sceneName], { timeout: 8000 });
        if (sceneName === 'history-composer') await stageHistoryComposer(page);
        await page.waitForTimeout(250);
        const file = path.join(outDir, `${sceneName}--${themeName}--${width}.png`);
        await page.screenshot({ path: file, fullPage: false });
        shots++;
        console.log('shot', path.basename(file));
      } catch (error) {
        failures.push(`${sceneName}/${themeName}/${width}: ${error.message.split('\n')[0]}`);
      }
      if (consoleErrors.length) failures.push(`${sceneName}/${themeName}/${width} console: ${consoleErrors.join(' | ')}`);
      await context.close();
    }
  }
}

// --- axe pass on a representative page (tool calls + decisions, dark) -------

{
  const context = await browser.newContext({ viewport: { width: 1440, height: HEIGHT } });
  const page = await context.newPage();
  await page.addInitScript(INIT_STUB);
  await page.goto(`${origin}/app/index.html`, { waitUntil: 'load' });
  await page.evaluate((events) => { for (const event of events) window.__psxEmit(event); },
    SCENES['permission-elicitation'](THEMES['vercel-dark']));
  await page.waitForSelector('.agent-decision-elicitation', { timeout: 8000 });
  const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = axe.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  fs.writeFileSync(path.join(outDir, 'axe-report.json'), JSON.stringify(axe.violations, null, 2));
  console.log(`axe: ${axe.violations.length} violations (${serious.length} serious/critical) — see axe-report.json`);
  if (serious.length) {
    for (const v of serious) failures.push(`axe ${v.impact}: ${v.id} (${v.nodes.length} nodes)`);
  }
  await context.close();
}

await browser.close();
server.close();

console.log(`\n${shots} screenshots written to ${outDir}`);
if (failures.length) {
  console.error('FAILURES:');
  for (const failure of failures) console.error(' -', failure);
  process.exit(1);
}
console.log('screenshot baseline: PASS');
