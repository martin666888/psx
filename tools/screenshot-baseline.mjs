// Deterministic Windows Chromium visual gate for the shipped WebView bundle.
//
// Normal mode compares against committed baselines and never creates them:
//   node tools/screenshot-baseline.mjs
// Explicit update mode rewrites baselines for human review:
//   node tools/screenshot-baseline.mjs --update

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { Buffer } from 'node:buffer';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const updateBaselines = process.argv.includes('--update');
const browserRoot = path.join(root, 'TestResults', 'playwright-browsers');

const [{ chromium }, { default: AxeBuilder }, { default: pixelmatch }, { PNG }] =
  await Promise.all([
    import('playwright'),
    import('@axe-core/playwright'),
    import('pixelmatch'),
    import('pngjs')
  ]);

const wwwroot = path.join(root, 'wwwroot');
const baselineDir = path.join(
  root,
  'tests',
  'PSX.Web.Tests',
  'visual-baselines',
  'windows-chromium'
);
const resultDir = path.join(root, 'TestResults', 'visual');
fs.mkdirSync(resultDir, { recursive: true });
if (updateBaselines) fs.mkdirSync(baselineDir, { recursive: true });

const HEIGHT = 900;
const MAX_PIXEL_DIFFERENCE_RATIO = 0.0015;
const AGENT_A = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const AGENT_B = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const TERMINAL = '11111111-1111-4111-8111-111111111111';
const LONG_UNBROKEN_DRAFT =
  'https://example.invalid/' + 'segment/'.repeat(600) + '?token=' + 'A'.repeat(4096);

function terminalOutput(text) {
  return Buffer.from(text, 'utf8').toString('base64');
}

function lockedChromiumExecutable() {
  const manifestPath = path.join(root, 'node_modules', 'playwright-core', 'browsers.json');
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const descriptor = manifest.browsers.find((browser) => browser.name === 'chromium');
  if (!descriptor?.revision) throw new Error('Playwright Chromium descriptor is missing');
  return path.join(
    browserRoot,
    'chromium-' + descriptor.revision,
    'chrome-win64',
    'chrome.exe'
  );
}

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
    const equals = line.indexOf('=');
    if (equals > 0 && current) {
      sections[current][line.slice(0, equals).trim()] = line.slice(equals + 1).trim();
    }
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
  dark: appearanceFromPreset(path.join(root, 'theme-presets', 'vercel-neutral-dark.ini')),
  light: appearanceFromPreset(path.join(root, 'theme-presets', 'vercel-neutral-light.ini'))
};

const PROVIDERS = [
  {
    key: 'claude-code',
    displayName: 'Claude Code',
    assistantName: 'Claude',
    isDefault: true,
    iconKey: 'claude'
  },
  {
    key: 'kimi-code',
    displayName: 'Kimi Code',
    assistantName: 'Kimi',
    isDefault: false,
    iconKey: 'kimi'
  }
];

const DAILY_TOKENS = Array.from({ length: 365 }, (_, index) =>
  index % 29 === 0 ? (index + 1) * 120 : 0
);

const INIT_STUB = ({ profile, providers, dailyTokens }) => {
  const listeners = [];
  const emit = (data) => listeners.forEach((listener) => listener({ data }));
  const reply = (data) => window.queueMicrotask(() => emit(data));

  window.localStorage.setItem('psx.agent.historyDockOpen', '0');
  window.localStorage.setItem('psx.agent.historyDockWidth', '300');
  window.__psxPosted = [];
  window.__psxEmit = emit;
  window.chrome = {
    webview: {
      postMessage(payload) {
        let message = payload;
        if (typeof message === 'string') {
          try {
            message = JSON.parse(message);
          } catch {
            return;
          }
        }
        window.__psxPosted.push(message);
        if (!message || typeof message !== 'object') return;
        const workspaceId = message.type === 'agent_command' ? message.workspaceId : undefined;
        if (message.command === 'history') {
          reply({
            type: 'agent_threads',
            ...(workspaceId ? { workspaceId } : {}),
            requestId: message.requestId,
            threads: [
              {
                threadId: 'thread-1',
                title: 'Prepare the PSX release and verify every local gate',
                cwd: 'D:/PSX-open-source',
                updatedAt: '2026-08-09T09:30:00Z',
                sessionId: 'session-1',
                provider: 'claude-code'
              },
              {
                threadId: 'thread-2',
                title: 'Investigate the responsive Composer alignment',
                cwd: 'D:/PSX-open-source',
                updatedAt: '2026-08-08T15:20:00Z',
                sessionId: 'session-2',
                provider: 'kimi-code'
              },
              {
                threadId: 'thread-3',
                title: 'Review runtime process cancellation cleanup',
                cwd: 'D:/other-workspace',
                updatedAt: '2026-08-07T13:10:00Z',
                sessionId: 'session-3',
                provider: 'unknown-provider'
              }
            ]
          });
        } else if (message.command === 'profile_get') {
          reply({
            type: 'agent_profile',
            requestId: message.requestId,
            revision: 7,
            displayName: profile,
            avatarDataUrl: null
          });
        } else if (message.command === 'usage_report') {
          const completeness = {
            status: 'available',
            reasons: [],
            expectedSessions: 3,
            matchedSessions: 3,
            skippedFiles: 0,
            badLines: 0,
            untrackedThreads: 0
          };
          reply({
            type: 'agent_usage_report',
            requestId: message.requestId,
            generatedAt: '2026-08-09T10:00:00Z',
            timezone: 'Asia/Shanghai',
            completeness,
            report: {
              heatmapStartDate: '2025-08-10',
              dailyTokens,
              today: { totalTokens: 28642 },
              last7Days: { totalTokens: 164908 },
              last30Days: { totalTokens: 684120 },
              providers: providers.map((provider, index) => ({
                providerKey: provider.key,
                displayName: provider.displayName,
                iconKey: provider.iconKey,
                dailyTokens: dailyTokens.map((value) =>
                  index === 0 ? Math.round(value * 0.7) : Math.round(value * 0.3)
                ),
                today: { totalTokens: index === 0 ? 20100 : 8542 },
                last7Days: { totalTokens: index === 0 ? 116000 : 48908 },
                last30Days: { totalTokens: index === 0 ? 480000 : 204120 },
                completeness
              }))
            }
          });
        } else if (message.command === 'config_report') {
          reply({
            type: 'agent_config_report',
            requestId: message.requestId,
            generatedAt: '2026-08-09T10:00:00Z',
            providers: providers.map((provider) => ({
              providerKey: provider.key,
              displayName: provider.displayName,
              iconKey: provider.iconKey,
              state: 'available',
              facts: [{ label: 'Runtime', value: 'Managed by PSX' }],
              models: [],
              mcpServers: [],
              skills: []
            }))
          });
        }
      },
      addEventListener(name, listener) {
        if (name === 'message') listeners.push(listener);
      }
    }
  };
};

function workspaceRecord(workspaceId, kind, columnId, title, iconKey = 'agent') {
  return {
    workspaceId,
    kind,
    title,
    iconKey,
    columnId,
    isActiveTab: true,
    canSplitRight: true,
    splitBlockedReason: '',
    canCollapse: true
  };
}

function layout(columns, focusedColumnId = columns[0].columnId) {
  return {
    type: 'workspace_layout',
    revision: 1,
    focusedColumnId,
    columns
  };
}

function column(columnId, workspaceId, kind, ratio) {
  return {
    columnId,
    tabs: [{ workspaceId, kind }],
    activeTabId: workspaceId,
    ratio
  };
}

function catalog(workspaces) {
  return {
    type: 'workspace_catalog',
    revision: 1,
    maxColumns: 3,
    providers: PROVIDERS,
    workspaces
  };
}

function agentCreated(workspaceId, providerKey, assistantName) {
  return {
    type: 'agent_workspace_created',
    workspaceId,
    providerKey,
    assistantName,
    providerDisplayName: assistantName + ' Code'
  };
}

function agentReady(workspaceId, cwd, assistantName) {
  return [
    {
      type: 'runtime_status',
      workspaceId,
      state: 'ready',
      message: 'Ready',
      canInstall: false,
      canCancel: false
    },
    {
      type: 'agent_state',
      workspaceId,
      status: 'ready',
      cwd,
      sessionId: 'session-' + workspaceId.slice(0, 4),
      assistantName,
      busy: false,
      isDraft: false,
      supportsImage: true,
      contextUsedTokens: 4200,
      contextWindowTokens: 200000
    },
    {
      type: 'agent_modes',
      workspaceId,
      currentModeId: 'manual',
      modes: [
        { id: 'manual', name: 'Manual' },
        { id: 'plan', name: 'Plan' }
      ]
    },
    {
      type: 'agent_config_options',
      workspaceId,
      options: [
        {
          id: 'model',
          name: 'Model',
          type: 'select',
          currentValue: 'default',
          options: [
            { value: 'default', name: 'Default (recommended)' },
            { value: 'fast', name: 'Fast' }
          ]
        },
        {
          id: 'effort',
          name: 'Effort',
          type: 'select',
          currentValue: 'high',
          options: [
            { value: 'medium', name: 'Medium' },
            { value: 'high', name: 'High' }
          ]
        },
        { id: 'fast', name: 'Fast mode', type: 'boolean', currentValue: false },
        {
          id: 'agent',
          name: 'Agent',
          type: 'select',
          currentValue: 'default',
          options: [{ value: 'default', name: 'Default' }]
        }
      ]
    }
  ];
}

function appEvents(theme) {
  return [
    { type: 'appearance_settings', settings: theme },
    { type: 'agent_providers', providers: PROVIDERS },
    {
      type: 'theme_catalog',
      revision: 1,
      currentTheme: 'vercel-neutral',
      themes: [
        { id: 'vercel-neutral', name: 'Vercel Neutral' },
        { id: 'gruvbox', name: 'Gruvbox' }
      ]
    }
  ];
}

function singleAgentEvents(theme, workspaceId = AGENT_A) {
  return [
    ...appEvents(theme),
    agentCreated(workspaceId, 'claude-code', 'Claude'),
    catalog([
      workspaceRecord(workspaceId, 'agent', 'column-1', 'PSX-open-source', 'claude')
    ]),
    layout([column('column-1', workspaceId, 'agent', 1)]),
    { type: 'workspace_activated', workspaceId, kind: 'agent' },
    ...agentReady(workspaceId, 'D:/PSX-open-source', 'Claude'),
    {
      type: 'user_message',
      workspaceId,
      text: 'Audit the release gate and explain what still needs attention.'
    },
    {
      type: 'assistant_delta',
      workspaceId,
      text: '**Current result:** the core checks are green.\n\n- Runtime cleanup is deterministic.\n- Global commands use one route.\n- Visual evidence is now reproducible.'
    },
    { type: 'run_finished', workspaceId }
  ];
}

function terminalOnlyEvents(theme) {
  return [
    ...appEvents(theme),
    { type: 'create', sessionId: TERMINAL },
    catalog([
      workspaceRecord(TERMINAL, 'terminal', 'column-1', 'PowerShell', 'terminal')
    ]),
    layout([column('column-1', TERMINAL, 'terminal', 1)]),
    { type: 'workspace_activated', workspaceId: TERMINAL, kind: 'terminal' },
    {
      type: 'output',
      sessionId: TERMINAL,
      data: terminalOutput(
        'Windows PowerShell\r\nCopyright (C) Microsoft Corporation.\r\n\r\nPS D:\\PSX-open-source> '
      )
    }
  ];
}

function splitAgentEvents(theme) {
  return [
    ...appEvents(theme),
    agentCreated(AGENT_A, 'claude-code', 'Claude'),
    agentCreated(AGENT_B, 'kimi-code', 'Kimi'),
    catalog([
      workspaceRecord(AGENT_A, 'agent', 'column-1', 'Claude review', 'claude'),
      workspaceRecord(AGENT_B, 'agent', 'column-2', 'Kimi implementation', 'kimi')
    ]),
    layout([
      column('column-1', AGENT_A, 'agent', 0.35),
      column('column-2', AGENT_B, 'agent', 0.65)
    ], 'column-2'),
    { type: 'workspace_activated', workspaceId: AGENT_B, kind: 'agent' },
    ...agentReady(AGENT_A, 'D:/PSX-open-source', 'Claude'),
    ...agentReady(AGENT_B, 'D:/PSX-open-source', 'Kimi'),
    {
      type: 'assistant_delta',
      workspaceId: AGENT_A,
      text: 'The narrow column intentionally wraps content without painting into its neighbor.'
    },
    { type: 'run_finished', workspaceId: AGENT_A },
    {
      type: 'assistant_delta',
      workspaceId: AGENT_B,
      text: 'The wider column keeps the full toolbar and the same Composer bottom edge.'
    },
    { type: 'run_finished', workspaceId: AGENT_B }
  ];
}

function mixedEvents(theme) {
  return [
    ...appEvents(theme),
    { type: 'create', sessionId: TERMINAL },
    agentCreated(AGENT_A, 'claude-code', 'Claude'),
    catalog([
      workspaceRecord(TERMINAL, 'terminal', 'column-1', 'PowerShell', 'terminal'),
      workspaceRecord(AGENT_A, 'agent', 'column-2', 'PSX-open-source', 'claude')
    ]),
    layout([
      column('column-1', TERMINAL, 'terminal', 0.46),
      column('column-2', AGENT_A, 'agent', 0.54)
    ], 'column-2'),
    { type: 'workspace_activated', workspaceId: AGENT_A, kind: 'agent' },
    ...agentReady(AGENT_A, 'D:/PSX-open-source', 'Claude'),
    {
      type: 'output',
      sessionId: TERMINAL,
      data: terminalOutput('PS D:\\PSX-open-source> powershell -File tools/test.ps1 -Suite Fast\r\n')
    },
    {
      type: 'assistant_delta',
      workspaceId: AGENT_A,
      text: 'History should push both columns as one layout, never overlay either workspace.'
    },
    { type: 'run_finished', workspaceId: AGENT_A }
  ];
}

function planEvents(theme) {
  return [
    ...singleAgentEvents(theme),
    {
      type: 'plan_update',
      workspaceId: AGENT_A,
      runId: 'run-plan',
      entries: [
        { content: 'Run deterministic unit and integration gates', status: 'completed' },
        { content: 'Compare both visual themes and all responsive boundaries', status: 'in_progress' },
        { content: 'Review generated evidence before release', status: 'pending' }
      ]
    }
  ];
}

function permissionEvents(theme) {
  return [
    ...singleAgentEvents(theme),
    {
      type: 'permission_request',
      workspaceId: AGENT_A,
      requestId: 'permission-1',
      title: 'Run the Full release gate?',
      description: 'This runs the desktop probes, release smoke checks, visual comparison and dependency audit.',
      options: [
        { optionId: 'allow', name: 'Allow once', kind: 'allow_once' },
        { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
      ]
    }
  ];
}

function elicitationEvents(theme) {
  return [
    ...singleAgentEvents(theme),
    {
      type: 'elicitation_request',
      workspaceId: AGENT_A,
      requestId: 'elicitation-1',
      message: 'Choose the release evidence to retain.',
      schema: {
        properties: {
          scope: {
            type: 'string',
            title: 'Evidence scope',
            oneOf: [
              { const: 'full', title: 'Full', description: 'Keep screenshots, diffs and audit reports.' },
              { const: 'summary', title: 'Summary', description: 'Keep only machine-readable summaries.' }
            ]
          },
          note: { type: 'string', title: 'Release note', default: 'Reviewed locally' }
        },
        required: ['scope']
      }
    }
  ];
}

const SCENES = [
  ...[720, 719, 520, 519, 400, 399].map((paneWidth) => ({
    name: 'responsive-' + paneWidth,
    // The logical column starts after the 40px activity rail; the rounded
    // Agent surface is inset 12px on both sides. Container-query breakpoints
    // belong to .agent-panel, so size that element (not the outer viewport)
    // to the exact contract boundary.
    viewportWidth: paneWidth + 66,
    events: singleAgentEvents,
    ready: '.agent-panel:not([hidden]) [data-role="input"]',
    async verify(page) {
      const probe = await page.locator('.agent-panel:not([hidden])').evaluate((panel) => {
        const config = panel.querySelector('.agent-config-toggle');
        const more = panel.querySelector('.agent-toolbar-more-trigger');
        return {
          width: panel.clientWidth,
          configDisplay: config ? window.getComputedStyle(config).display : 'missing',
          moreDisplay: more ? window.getComputedStyle(more).display : 'missing',
          right: panel.getBoundingClientRect().right,
          viewport: window.innerWidth
        };
      });
      if (Math.abs(probe.width - paneWidth) > 1) {
        throw new Error(`expected ${paneWidth}px Agent pane, received ${probe.width}px`);
      }
      if (probe.right > probe.viewport + 1) throw new Error('Agent pane overflows the viewport');
      const configVisible = probe.configDisplay !== 'none';
      const moreVisible = probe.moreDisplay !== 'none';
      if (configVisible !== (paneWidth <= 719)) {
        throw new Error(`Composer config visibility is wrong at ${paneWidth}px`);
      }
      if (moreVisible !== (paneWidth <= 519)) {
        throw new Error(`Toolbar More visibility is wrong at ${paneWidth}px`);
      }
      if (paneWidth === 719) await verifyComposerDismissal(page);
      if (paneWidth === 519) await verifyToolbarDismissal(page);
    }
  })),
  {
    name: 'composer-long-unbroken-paste',
    viewportWidth: 1200,
    events: singleAgentEvents,
    ready: `.agent-panel[data-workspace-id="${AGENT_A}"] [data-role="input"]`,
    clipboard: true,
    geometryOnly: true,
    async stage(page) {
      const input = page.locator(
        `.agent-panel[data-workspace-id="${AGENT_A}"] [data-role="input"]`
      );
      await input.click();
      await page.evaluate(
        (draft) => window.navigator.clipboard.writeText(draft),
        LONG_UNBROKEN_DRAFT
      );
      await page.keyboard.press('Control+V');
      await page.waitForFunction(
        (draft) => document.querySelector('[data-role="input"]')?.value === draft,
        LONG_UNBROKEN_DRAFT
      );
    },
    async verify(page) {
      const probe = await page
        .locator(`.agent-panel[data-workspace-id="${AGENT_A}"]`)
        .evaluate((panel) => {
          const rect = (element) => {
            const bounds = element?.getBoundingClientRect();
            return bounds
              ? {
                  left: bounds.left,
                  right: bounds.right,
                  width: bounds.width
                }
              : null;
          };
          return {
            panel: rect(panel),
            panelClientWidth: panel.clientWidth,
            panelScrollWidth: panel.scrollWidth,
            workspace: rect(panel.querySelector('.agent-workspace')),
            readingColumn: rect(panel.querySelector('.agent-thread-content > .agent-turn')),
            composer: rect(panel.querySelector('.agent-composer-main')),
            input: rect(panel.querySelector('[data-role="input"]')),
            pageScrollX: window.scrollX
          };
        });

      const { panel, workspace, readingColumn, composer, input } = probe;
      if (!panel || !workspace || !readingColumn || !composer || !input) {
        throw new Error(`long-paste geometry is incomplete: ${JSON.stringify(probe)}`);
      }
      if (probe.panelScrollWidth > probe.panelClientWidth + 1) {
        throw new Error(
          `long paste widened the panel: ${probe.panelScrollWidth}px > ${probe.panelClientWidth}px`
        );
      }
      for (const [name, bounds] of [
        ['workspace', workspace],
        ['reading column', readingColumn],
        ['Composer', composer],
        ['input', input]
      ]) {
        if (bounds.left < panel.left - 1 || bounds.right > panel.right + 1) {
          throw new Error(`${name} escaped the Agent pane: ${JSON.stringify({ panel, bounds })}`);
        }
      }
      // Conversation reserves a native scrollbar gutter while Composer does
      // not. Centering inside those two available widths can offset either
      // edge by up to one scrollbar, but pasted content must not add drift.
      if (Math.abs(readingColumn.left - composer.left) > 10) {
        throw new Error(
          `Conversation and Composer starts diverged: ${readingColumn.left}px vs ${composer.left}px`
        );
      }
      // Conversation reserves the native scrollbar gutter while Composer does
      // not. Their trailing edges may therefore differ by one scrollbar, but
      // never by content-driven grid expansion.
      if (Math.abs(readingColumn.right - composer.right) > 10) {
        throw new Error(
          `Conversation and Composer ends diverged: ${readingColumn.right}px vs ${composer.right}px`
        );
      }
      if (probe.pageScrollX !== 0) {
        throw new Error(`long paste horizontally scrolled the page to ${probe.pageScrollX}px`);
      }
    }
  },
  {
    name: 'split-agents',
    viewportWidth: 1000,
    events: splitAgentEvents,
    ready: `.agent-panel[data-workspace-id="${AGENT_B}"] [data-role="input"]`,
    async verify(page) {
      const bottoms = await page.locator('.agent-panel:not([hidden]) .agent-composer-card').evaluateAll(
        (cards) => cards.map((card) => card.getBoundingClientRect().bottom)
      );
      if (bottoms.length !== 2 || Math.abs(bottoms[0] - bottoms[1]) > 1) {
        throw new Error(`split Composer bottom edges differ: ${JSON.stringify(bottoms)}`);
      }
    }
  },
  {
    name: 'terminal-history-profile',
    viewportWidth: 1200,
    events: terminalOnlyEvents,
    ready: '.workspace-tab',
    async stage(page) {
      await page.evaluate(async () => {
        const probe = document.createElement('code');
        probe.className = 'font-mono';
        probe.textContent = 'terminal mono probe';
        probe.style.cssText = 'position:fixed;left:-10000px;top:0;';
        document.body.appendChild(probe);
        await document.fonts.ready;
      });
      const terminalFontRequests = await page.evaluate(() =>
        performance.getEntriesByType('resource')
          .map((entry) => entry.name)
          .filter((name) => name.includes('/vendor/fonts/maple-mono/'))
      );
      if (terminalFontRequests.length !== 0) {
        throw new Error(`Terminal-only startup loaded Agent fonts: ${terminalFontRequests.join(', ')}`);
      }
      await page.locator('[data-role="history-toggle"]').click();
      await page.waitForSelector('.agent-history-item');
      await page.evaluate(() => document.fonts.ready);
      await page.waitForFunction(() => {
        const dock = document.querySelector('[data-role="history-dock"]')?.getBoundingClientRect();
        const paneRoot = document.querySelector('#workspace-panes')?.getBoundingClientRect();
        return !!dock && !!paneRoot && Math.abs(paneRoot.left - dock.right - 12) <= 1;
      });
      const historyFontRequests = await page.evaluate(() =>
        performance.getEntriesByType('resource')
          .map((entry) => entry.name)
          .filter((name) => name.includes('/vendor/fonts/maple-mono/'))
      );
      if (historyFontRequests.length !== 0) {
        throw new Error(`Natural-language History loaded mono fonts: ${historyFontRequests.join(', ')}`);
      }
      await page.evaluate(async () => {
        const probe = document.createElement('div');
        probe.dataset.agentFontSurface = '';
        probe.style.cssText = 'position:fixed;left:-10000px;top:0;';
        probe.innerHTML = '<code class="font-mono" style="font-weight:400">mono regular</code><code class="font-mono" style="font-weight:600">mono semibold</code>';
        document.body.appendChild(probe);
        await Promise.all([
          document.fonts.load('400 16px "PSX Maple Mono"'),
          document.fonts.load('600 16px "PSX Maple Mono"'),
          document.fonts.ready
        ]);
      });
    },
    async verify(page) {
      const probe = await page.evaluate(() => ({
        icons: [...document.querySelectorAll('.agent-history-provider-icon svg')]
          .map((icon) => icon.getAttribute('data-icon')),
        fontRequests: performance.getEntriesByType('resource')
          .map((entry) => entry.name)
          .filter((name) => name.includes('/vendor/fonts/maple-mono/'))
      }));
      for (const expected of ['claude', 'kimi', 'agent']) {
        if (!probe.icons.includes(expected)) {
          throw new Error(`provider icon ${expected} did not render from the catalog: ${probe.icons.join(', ')}`);
        }
      }
      for (const face of ['Regular', 'SemiBold']) {
        const matches = probe.fontRequests.filter((name) => name.includes(`-${face}.ttf`));
        if (matches.length !== 1) {
          throw new Error(`Maple Mono ${face} request count is ${matches.length}, expected 1`);
        }
      }
    }
  },
  {
    name: 'mixed-history-push',
    viewportWidth: 1600,
    events: mixedEvents,
    ready: `.agent-panel[data-workspace-id="${AGENT_A}"] [data-role="input"]`,
    async stage(page) {
      await page.locator('[data-role="history-toggle"]').click();
      await page.waitForSelector('.agent-history-item');
      await page.waitForFunction(() => {
        const dock = document.querySelector('[data-role="history-dock"]')?.getBoundingClientRect();
        const paneRoot = document.querySelector('#workspace-panes')?.getBoundingClientRect();
        return !!dock && !!paneRoot && Math.abs(paneRoot.left - dock.right - 12) <= 1;
      });
    },
    async verify(page) {
      const gap = await page.evaluate(() => {
        const dock = document.querySelector('[data-role="history-dock"]')?.getBoundingClientRect();
        const paneRoot = document.querySelector('#workspace-panes')?.getBoundingClientRect();
        return dock && paneRoot ? paneRoot.left - dock.right : Number.NaN;
      });
      if (Math.abs(gap - 12) > 1) throw new Error(`History/pane gap is ${gap}px, expected 12px`);
      await verifyHistoryHeaderWidths(page);
    }
  },
  {
    name: 'history-header-220',
    viewportWidth: 1200,
    events: terminalOnlyEvents,
    ready: '.workspace-tab',
    async stage(page) {
      await page.locator('[data-role="history-toggle"]').click();
      await page.waitForSelector('[data-role="history-title"]');
      await page.waitForSelector('.agent-history-item');
      await setHistoryDockWidth(page, 220);
    },
    async verify(page) {
      await assertHistoryHeaderGeometry(page, 220);
    }
  },
  {
    name: 'plan-wide',
    viewportWidth: 840,
    events: planEvents,
    ready: '.agent-plan-panel',
    async verify(page) {
      const visible = await page.locator('[data-role="plan-card"]').isVisible();
      if (!visible) throw new Error('wide Plan card is not visible');
      await verifyPlanToggle(page);
    }
  },
  {
    name: 'plan-narrow',
    viewportWidth: 559,
    events: planEvents,
    ready: '.agent-toolbar-more-trigger',
    async stage(page) {
      await page.locator('.agent-toolbar-more-trigger').click();
      await page.locator('[data-role="plan-toggle"]').click();
      await page.waitForSelector('[data-role="plan-card"]:not([hidden])');
    },
    async verify(page) {
      if (!(await page.locator('[data-role="plan-card"]').isVisible())) {
        throw new Error('narrow Plan overlay is not visible');
      }
      await verifyPlanToggle(page);
    }
  },
  {
    name: 'permission',
    viewportWidth: 1080,
    events: permissionEvents,
    ready: '[data-request-id="permission-1"]'
  },
  {
    name: 'elicitation',
    viewportWidth: 1080,
    events: elicitationEvents,
    ready: '[data-request-id="elicitation-1"]'
  },
  {
    name: 'usage-dialog',
    viewportWidth: 1200,
    events: terminalOnlyEvents,
    ready: '.workspace-tab',
    async stage(page) {
      await page.locator('[data-role="app-settings-toggle"]').click();
      await page.waitForSelector('[data-role="usage-panel"]');
      await page.keyboard.press('Escape');
      await page.locator('[data-role="usage-panel"]').waitFor({ state: 'detached' });
      await page.waitForFunction(() => {
        const button = document.querySelector('[data-role="app-settings-toggle"]');
        return button && document.activeElement === button;
      });
      await page.locator('[data-role="app-settings-toggle"]').click();
      await page.waitForSelector('[data-role="usage-panel"]');
    },
    async verify(page) {
      const panel = page.locator('[data-role="usage-panel"]');
      if (!(await panel.isVisible())) throw new Error('Usage dialog did not open');
      await page.keyboard.press('Escape');
      await panel.waitFor({ state: 'detached' });
      await page.waitForFunction(() => {
        const button = document.querySelector('[data-role="app-settings-toggle"]');
        return button && document.activeElement === button;
      });
      await page.locator('[data-role="app-settings-toggle"]').click();
      await page.waitForSelector('[data-role="usage-panel"]');
    }
  }
];

async function setHistoryDockWidth(page, width) {
  await page.evaluate((nextWidth) => {
    const container = document.querySelector('#agent-workspace-container')
      || document.querySelector('#agents');
    if (!container) throw new Error('history container missing');
    container.style.setProperty('--agent-history-width', nextWidth + 'px');
  }, width);
  await page.waitForFunction((nextWidth) => {
    const dock = document.querySelector('[data-role="history-dock"]');
    return !!dock && !dock.hidden && Math.abs(dock.getBoundingClientRect().width - nextWidth) <= 1;
  }, width);
}

async function assertHistoryHeaderGeometry(page, width) {
  const probe = await page.evaluate(() => {
    const box = (element) => {
      const bounds = element?.getBoundingClientRect();
      return bounds
        ? {
          top: bounds.top,
          bottom: bounds.bottom,
          left: bounds.left,
          right: bounds.right,
          width: bounds.width,
          height: bounds.height
        }
        : null;
    };
    const dock = document.querySelector('[data-role="history-dock"]');
    const bar = dock?.querySelector('.agent-history-dock-bar');
    const title = dock?.querySelector('[data-role="history-title"]');
    const heading = title?.querySelector('.agent-history-heading-text');
    const refresh = dock?.querySelector('[data-role="history-refresh"]');
    const search = dock?.querySelector('[data-role="history-search"]');
    const filter = dock?.querySelector('[data-role="history-provider-filter"]');
    return {
      dock: box(dock),
      bar: box(bar),
      title: box(title),
      heading: box(heading),
      refresh: box(refresh),
      search: box(search),
      filter: box(filter),
      headingLines: heading ? heading.getClientRects().length : 0
    };
  });
  if (!probe.dock || !probe.bar || !probe.title || !probe.heading || !probe.refresh || !probe.search || !probe.filter) {
    throw new Error(`History header nodes missing at ${width}px`);
  }
  if (Math.abs(probe.dock.width - width) > 1) {
    throw new Error(`History dock width is ${probe.dock.width}px, expected ${width}px`);
  }
  if (Math.abs(probe.title.top - probe.refresh.top) > 2) {
    throw new Error(`title and refresh wrapped at ${width}px`);
  }
  if (probe.refresh.left + 1 < probe.heading.right) {
    throw new Error(`refresh overlapped the title at ${width}px`);
  }
  if (probe.headingLines !== 1 || probe.heading.height > 34) {
    throw new Error(`history title wrapped at ${width}px (${probe.headingLines} lines, ${probe.heading.height}px)`);
  }
  if (probe.search.top + 1 < probe.title.bottom) {
    throw new Error(`search did not stack below the title at ${width}px`);
  }
  if (probe.filter.top + 1 < probe.search.bottom) {
    throw new Error(`filter did not stack below search at ${width}px`);
  }
  if (probe.search.height > 40 || probe.filter.height > 40 || probe.refresh.height > 36) {
    throw new Error(`History header control wrapped at ${width}px`);
  }
  if (probe.bar.height > 160) {
    throw new Error(`History header is ${probe.bar.height}px at ${width}px, expected a three-row stack`);
  }
  if (probe.search.width + 28 < probe.bar.width) {
    throw new Error(`search is not full width at ${width}px`);
  }
}

async function verifyHistoryHeaderWidths(page) {
  const original = await page.evaluate(() => {
    const container = document.querySelector('#agent-workspace-container')
      || document.querySelector('#agents');
    const value = parseFloat(container?.style.getPropertyValue('--agent-history-width') || '');
    return Number.isFinite(value) ? value : 300;
  });
  for (const width of [220, 280, 420]) {
    await setHistoryDockWidth(page, width);
    await assertHistoryHeaderGeometry(page, width);
  }
  await setHistoryDockWidth(page, original);
}

async function verifyComposerDismissal(page) {
  const trigger = page.locator('.agent-config-toggle');
  const popover = page.locator('.agent-config-popover');
  await trigger.click();
  if ((await popover.getAttribute('data-open')) !== 'true') throw new Error('Composer config did not open');
  await page.locator('.agent-toolbar').click({ position: { x: 8, y: 8 } });
  if ((await popover.getAttribute('data-open')) !== 'false') throw new Error('Composer config ignored outside click');
  await trigger.click();
  await page.keyboard.press('Escape');
  if ((await popover.getAttribute('data-open')) !== 'false') throw new Error('Composer config ignored Escape');
  if (!(await trigger.evaluate((button) => document.activeElement === button))) {
    throw new Error('Composer config did not restore trigger focus');
  }
}

async function verifyToolbarDismissal(page) {
  const trigger = page.locator('.agent-toolbar-more-trigger');
  const root = page.locator('.agent-toolbar-more');
  await trigger.click();
  if ((await root.getAttribute('data-open')) !== 'true') throw new Error('Toolbar More did not open');
  await page.locator('.agent-toolbar').click({ position: { x: 8, y: 8 } });
  if ((await root.getAttribute('data-open')) !== 'false') throw new Error('Toolbar More ignored outside click');
  await trigger.click();
  await page.keyboard.press('Escape');
  if ((await root.getAttribute('data-open')) !== 'false') throw new Error('Toolbar More ignored Escape');
  if (!(await trigger.evaluate((button) => document.activeElement === button))) {
    throw new Error('Toolbar More did not restore trigger focus');
  }
}

async function verifyPlanToggle(page) {
  const trigger = page.locator('[data-role="plan-toggle"]');
  await trigger.click();
  if ((await trigger.getAttribute('aria-expanded')) !== 'false') throw new Error('Plan did not close');
  await trigger.click();
  if ((await trigger.getAttribute('aria-expanded')) !== 'true') throw new Error('Plan did not reopen');
}

async function verifyGlobalDismissals(page) {
  const input = page.locator('[data-role="input"]');
  const create = page.locator('[data-role="workspace-create-toggle"]');
  await create.click();
  await page.waitForSelector('.workspace-popover-create');
  await input.click();
  if (await page.locator('.workspace-popover-create').count()) throw new Error('Create ignored outside click');
  await create.click();
  await page.keyboard.press('Escape');
  if (await page.locator('.workspace-popover-create').count()) throw new Error('Create ignored Escape');
  if (!(await create.evaluate((button) => document.activeElement === button))) {
    throw new Error('Create did not restore trigger focus');
  }

  const theme = page.locator('[data-role="theme-toggle"]');
  await theme.click();
  await page.waitForSelector('.workspace-popover-theme');
  await input.click();
  if (await page.locator('.workspace-popover-theme').count()) throw new Error('Theme ignored outside click');
  await theme.click();
  await page.keyboard.press('Escape');
  if (await page.locator('.workspace-popover-theme').count()) throw new Error('Theme ignored Escape');
  if (!(await theme.evaluate((button) => document.activeElement === button))) {
    throw new Error('Theme did not restore trigger focus');
  }
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ttf': 'font/ttf',
  '.woff2': 'font/woff2'
};

const server = http.createServer((request, response) => {
  const requestUrl = new URL(request.url, 'http://127.0.0.1');
  const relative = decodeURIComponent(requestUrl.pathname).replace(/^\/+/, '');
  const file = path.resolve(wwwroot, relative);
  if (
    !file.startsWith(wwwroot + path.sep) ||
    !fs.existsSync(file) ||
    !fs.statSync(file).isFile()
  ) {
    response.writeHead(404).end('not found');
    return;
  }
  response.writeHead(200, {
    'content-type': MIME[path.extname(file)] || 'application/octet-stream',
    'cache-control': 'no-store'
  });
  response.end(fs.readFileSync(file));
});

function compareOrUpdate(name, actualPath) {
  const expectedPath = path.join(baselineDir, name + '.png');
  if (updateBaselines) {
    fs.copyFileSync(actualPath, expectedPath);
    return { updated: true, differentPixels: 0, ratio: 0 };
  }
  if (!fs.existsSync(expectedPath)) {
    throw new Error(`missing visual baseline ${path.relative(root, expectedPath)}; run npm run update:visual`);
  }
  const actual = PNG.sync.read(fs.readFileSync(actualPath));
  const expected = PNG.sync.read(fs.readFileSync(expectedPath));
  if (actual.width !== expected.width || actual.height !== expected.height) {
    throw new Error(
      `image dimensions changed: expected ${expected.width}x${expected.height}, ` +
      `received ${actual.width}x${actual.height}`
    );
  }
  const diff = new PNG({ width: actual.width, height: actual.height });
  const differentPixels = pixelmatch(
    expected.data,
    actual.data,
    diff.data,
    actual.width,
    actual.height,
    { threshold: 0.1, includeAA: false }
  );
  const ratio = differentPixels / (actual.width * actual.height);
  if (differentPixels > 0) {
    fs.writeFileSync(path.join(resultDir, name + '--diff.png'), PNG.sync.write(diff));
  }
  if (ratio > MAX_PIXEL_DIFFERENCE_RATIO) {
    throw new Error(
      `${differentPixels} pixels differ (${(ratio * 100).toFixed(4)}%, limit 0.15%)`
    );
  }
  return { updated: false, differentPixels, ratio };
}

async function waitForStableUi(page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  });
}

const failures = [];
let browser;
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;

try {
  const executablePath = lockedChromiumExecutable();
  if (!fs.existsSync(executablePath)) {
    throw new Error(`locked Chromium is missing at ${executablePath}`);
  }
  browser = await chromium.launch({ headless: true, executablePath });
  for (const [themeName, theme] of Object.entries(THEMES)) {
    for (const scene of SCENES) {
      const name = `${scene.name}--${themeName}`;
      let context;
      try {
        context = await browser.newContext({
          viewport: { width: scene.viewportWidth, height: HEIGHT },
          deviceScaleFactor: 1,
          locale: 'zh-CN',
          colorScheme: themeName,
          reducedMotion: 'reduce'
        });
        if (scene.clipboard) {
          await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin });
        }
        const page = await context.newPage();
        const runtimeErrors = [];
        page.on('console', (message) => {
          if (message.type() === 'error') runtimeErrors.push('console: ' + message.text());
        });
        page.on('pageerror', (error) => runtimeErrors.push('page: ' + String(error)));
        page.on('requestfailed', (request) =>
          runtimeErrors.push(`request: ${request.url()} (${request.failure()?.errorText || 'failed'})`)
        );
        page.on('response', (response) => {
          if (response.status() >= 400) {
            runtimeErrors.push(`response: ${response.status()} ${response.url()}`);
          }
        });

        await page.addInitScript(INIT_STUB, {
          profile: 'Minghai',
          providers: PROVIDERS,
          dailyTokens: DAILY_TOKENS
        });
        await page.goto(`${origin}/app/index.html`, { waitUntil: 'load' });
        await page.evaluate((events) => {
          for (const event of events) window.__psxEmit(event);
        }, scene.events(theme));
        await page.waitForSelector(scene.ready, { timeout: 10000 });
        if (scene.stage) await scene.stage(page);
        if (scene.name === 'responsive-720') await verifyGlobalDismissals(page);
        if (scene.verify) await scene.verify(page);
        await waitForStableUi(page);
        if (runtimeErrors.length) throw new Error(runtimeErrors.join(' | '));

        if (scene.geometryOnly) {
          console.log(`PASS ${name}: locked-Chromium geometry contract`);
          continue;
        }

        const axe = await new AxeBuilder({ page })
          .withTags(['wcag2a', 'wcag2aa'])
          .analyze();
        const serious = axe.violations.filter(
          (violation) => violation.impact === 'serious' || violation.impact === 'critical'
        );
        fs.writeFileSync(
          path.join(resultDir, name + '--axe.json'),
          JSON.stringify(axe.violations, null, 2)
        );
        if (serious.length) {
          throw new Error(
            'axe serious/critical: ' +
            serious.map((violation) => `${violation.id} (${violation.nodes.length})`).join(', ')
          );
        }
        const actualPath = path.join(resultDir, name + '--actual.png');
        await page.screenshot({ path: actualPath, fullPage: false, animations: 'disabled' });
        const result = compareOrUpdate(name, actualPath);
        const suffix = result.updated
          ? 'baseline updated'
          : `${result.differentPixels} changed pixels`;
        console.log(`PASS ${name}: ${suffix}; axe ${axe.violations.length} total`);
      } catch (error) {
        failures.push(`${name}: ${error instanceof Error ? error.message : String(error)}`);
      } finally {
        await context?.close();
      }
    }
  }
} catch (error) {
  failures.push(
    'browser startup: ' +
    (error instanceof Error ? error.message : String(error)) +
    `; run npm run prepare:visual first (browser path: ${browserRoot})`
  );
} finally {
  await browser?.close();
  await new Promise((resolve) => server.close(resolve));
}

if (failures.length) {
  console.error('\nVisual gate failures:');
  for (const failure of failures) console.error(' - ' + failure);
  process.exitCode = 1;
} else {
  const themeCount = Object.keys(THEMES).length;
  const screenshotCount = SCENES.filter((scene) => !scene.geometryOnly).length * themeCount;
  const geometryCount = SCENES.filter((scene) => scene.geometryOnly).length * themeCount;
  console.log(
    `\nVisual gate passed: ${screenshotCount} screenshots, ${geometryCount} geometry checks, ` +
    `threshold ${(MAX_PIXEL_DIFFERENCE_RATIO * 100).toFixed(2)}%.`
  );
}
