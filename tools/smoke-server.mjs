// smoke-server.mjs — serves an unpacked Release ZIP's wwwroot over local HTTP
// for the Release browser smoke. The Vite output under wwwroot/app uses
// absolute /app/ URLs, so a plain static server maps the WebView2 virtual-host
// layout one-to-one. `?smoke=react` injects an isolated host-bridge fixture
// into /app/index.html and POSTs the result to /smoke-result.
//
// Usage: node tools/smoke-server.mjs <wwwroot-dir> [port]

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const rootDir = path.resolve(process.argv[2] || 'bin/smoke-unpack/wwwroot');
const port = Number(process.argv[3] || 8123);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8'
};

const REACT_SMOKE_BOOTSTRAP = String.raw`
(() => {
  const result = { errors: [], warnings: [], unhandled: [], checks: {} };
  const text = (value) => value instanceof Error ? value.message : String(value ?? '');
  const originalError = console.error.bind(console);
  const originalWarn = console.warn.bind(console);
  console.error = (...args) => {
    result.errors.push(args.map(text).join(' '));
    originalError(...args);
  };
  console.warn = (...args) => {
    result.warnings.push(args.map(text).join(' '));
    originalWarn(...args);
  };
  addEventListener('error', (event) => result.unhandled.push(text(event.error || event.message)));
  addEventListener('unhandledrejection', (event) => result.unhandled.push(text(event.reason)));

  const hostListeners = [];
  window.chrome = {
    webview: {
      postMessage(payload) {
        // Bridge.sendToHost posts a JSON string. The history broker only
        // accepts agent_threads answers to its own in-flight history
        // command, so the fixture echoes the requestId like the host.
        let message = payload;
        if (typeof message === 'string') {
          try { message = JSON.parse(message); } catch { return; }
        }
        if (message && message.type === 'agent_global_command' && message.command === 'history') {
          setTimeout(() => emit({
            type: 'agent_threads',
            requestId: message.requestId,
            threads: [{
              threadId: 'ht1',
              title: 'Release smoke thread',
              cwd: 'D:/smoke',
              updatedAt: '2026-07-20 10:00:00Z',
              sessionId: '',
              provider: 'claude-code',
              providerKey: 'claude-code'
            }]
          }), 0);
        }
      },
      addEventListener(name, listener) {
        if (name === 'message') hostListeners.push(listener);
      }
    }
  };
  const emit = (data) => hostListeners.forEach((listener) => listener({ data }));
  const waitFor = async (predicate, timeout = 6000) => {
    const started = performance.now();
    while (performance.now() - started < timeout) {
      if (predicate()) return true;
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    return false;
  };

  addEventListener('load', async () => {
    const workspaceId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    // Open the history dock up front so the CP4-4 list swap is exercised.
    window.localStorage.setItem('psx.agent.historyDockOpen', '1');
    emit({
      type: 'appearance_settings',
      settings: {
        agentFontSize: 15,
        themeColors: { background: '#ffffff', text: '#171717', border: '#e5e5e5' }
      }
    });
    emit({
      type: 'agent_providers',
      providers: [{
        id: 'claude-code',
        displayName: 'Claude Code',
        iconKey: 'claude',
        assistantName: 'Claude'
      }]
    });
    emit({
      type: 'agent_workspace_created',
      workspaceId,
      providerKey: 'claude-code',
      assistantName: 'Claude',
      providerDisplayName: 'Claude Code'
    });
    emit({
      type: 'workspace_catalog',
      revision: 1,
      maxColumns: 3,
      providers: [{
        id: 'claude-code',
        displayName: 'Claude Code',
        iconKey: 'claude',
        assistantName: 'Claude'
      }],
      workspaces: [{
        workspaceId,
        kind: 'agent',
        title: 'Release smoke',
        iconKey: 'claude',
        columnId: 'column-1',
        isActiveTab: true,
        canSplitRight: true,
        splitBlockedReason: '',
        canCollapse: false
      }]
    });
    // Ordered columns are the sole visibility truth. Use the production wire
    // shape so the packaged smoke cannot pass through legacy pane fallbacks.
    emit({
      type: 'workspace_layout',
      revision: 1,
      focusedColumnId: 'column-1',
      columns: [{
        columnId: 'column-1',
        tabs: [{ workspaceId, kind: 'agent' }],
        activeTabId: workspaceId,
        ratio: 1
      }]
    });
    emit({ type: 'workspace_activated', workspaceId, kind: 'agent' });
    emit({
      type: 'runtime_status',
      workspaceId,
      state: 'missing',
      message: 'Smoke runtime',
      canInstall: true,
      canCancel: false
    });
    emit({
      type: 'agent_state',
      workspaceId,
      status: 'ready',
      cwd: 'D:/smoke',
      sessionId: 'smoke-session',
      busy: false,
      isDraft: false,
      supportsImage: true,
      contextUsedTokens: 10,
      contextWindowTokens: 100
    });
    emit({ type: 'user_message', workspaceId, text: 'React release smoke' });
    emit({ type: 'thinking_started', workspaceId });
    emit({ type: 'thinking_delta', workspaceId, text: 'considering the release' });
    emit({ type: 'thinking_finished', workspaceId });
    emit({
      type: 'tool_started',
      workspaceId,
      runId: 'smoke-run',
      toolCallId: 'tc1',
      name: 'Bash',
      summary: 'run tests',
      input: 'npm test'
    });
    emit({ type: 'tool_delta', workspaceId, toolCallId: 'tc1', text: '\nok' });
    emit({ type: 'tool_finished', workspaceId, toolCallId: 'tc1', status: 'completed' });
    emit({
      type: 'plan_update',
      workspaceId,
      runId: 'smoke-run',
      entries: [{ content: 'Verify release', status: 'in_progress' }]
    });
    emit({
      type: 'permission_request',
      workspaceId,
      requestId: 'perm1',
      title: 'Run tool?',
      text: 'npm test',
      options: [
        { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
        { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
      ]
    });

    const mounted = await waitFor(() => {
      const panel = document.querySelector('.agent-panel[data-workspace-id="' + workspaceId + '"]');
      return panel
        && panel.querySelector('[data-role="conversation-host"]')?.dataset.islandState === 'mounted'
        && panel.querySelector('[data-role="toolbar-host"]')?.dataset.islandState === 'mounted'
        && panel.querySelector('[data-role="context-usage-host"]')?.dataset.islandState === 'mounted'
        && panel.querySelector('[data-role="runtime-host"]')?.dataset.islandState === 'mounted'
        && panel.querySelector('[data-role="plan-card"]')?.dataset.islandState === 'mounted';
    });
    const panel = document.querySelector('.agent-panel[data-workspace-id="' + workspaceId + '"]');
    result.checks.islandsMounted = mounted;
    result.checks.realTimelineNode =
      panel?.querySelector('.agent-message-user')?.textContent.includes('React release smoke') === true;
    result.checks.realRuntimeNode =
      panel?.querySelector('[data-role="runtime-card"]')?.dataset.state === 'missing';
    result.checks.realThinkingBlock =
      panel?.querySelector('.agent-thinking-block')?.textContent.includes('considering the release') === true;
    result.checks.realToolCard =
      panel?.querySelector('.agent-tool-card[data-tool-id="tc1"]')?.dataset.state === 'done';
    result.checks.realDecisionCard =
      panel?.querySelector('.agent-decision-permission[data-request-id="perm1"] [data-option-id="allow"]')
        ?.disabled === false;
    // Narrow viewports responsively collapse the dock; the toggle re-opens it
    // exactly like a user would.
    const dock = document.querySelector('.agent-history-dock');
    if (dock?.hidden) document.querySelector('[data-role="history-toggle"]')?.click();
    await waitFor(() => !!document.querySelector('.agent-history-item[data-thread-id="ht1"]'));
    result.checks.realHistoryThread =
      document.querySelector('.agent-history-item[data-thread-id="ht1"]')
        ?.textContent.includes('Release smoke thread') === true
      && !!document.querySelector('.agent-history-group-header svg');

    // The packaged Vite output must serve React and the Agent app as hashed
    // dynamic chunks from /app/assets/.
    const resources = performance.getEntriesByType('resource').map((entry) => entry.name);
    result.checks.reactVendorChunkLoaded =
      resources.some((name) => /\/app\/assets\/react-vendor-[-\w]+\.js(?:$|\?)/.test(name));
    result.checks.agentChunkLoaded =
      resources.some((name) => /\/app\/assets\/entry-[-\w]+\.js(?:$|\?)/.test(name));
    result.checks.markdownCoreLoaded =
      resources.some((name) => /\/app\/assets\/markdown-core-[-\w]+\.js(?:$|\?)/.test(name));
    result.checks.heavyMarkdownChunksDeferred =
      !resources.some((name) => /\/app\/assets\/markdown-(?:code|math|mermaid|katex)-[-\w]+\.(?:js|css)(?:$|\?)/.test(name));

    emit({ type: 'user_message', workspaceId, text: 'Highlight this local JavaScript sample.' });
    emit({
      type: 'assistant_delta',
      workspaceId,
      text: '\n\n' + String.fromCharCode(96).repeat(3)
        + 'js\nconst psxMarkdownWorker = true;\n'
        + String.fromCharCode(96).repeat(3)
    });
    emit({ type: 'run_finished', workspaceId });
    // Edge resource timing omits the module worker and markdown-code chunk;
    // the Shiki token style plus source text prove the packaged highlighter ran.
    // Chunk files themselves are gated by tools/web-build-lib.mjs and
    // tools/markdown-performance.mjs.
    result.checks.shikiWorkerHighlighted = await waitFor(() => {
      const block = panel?.querySelector('[data-streamdown="code-block-body"]');
      return !!block?.querySelector('[style*="--sdm-c"]')
        && block.textContent.includes('psxMarkdownWorker');
    }, 15000);

    const passed =
      Object.values(result.checks).every((value) => value === true)
      && result.errors.length === 0
      && result.warnings.length === 0
      && result.unhandled.length === 0;
    document.body.dataset.smokeStatus = passed ? 'pass' : 'fail';
    const payload = JSON.stringify({ passed, ...result });
    const report = document.createElement('pre');
    report.id = 'react-smoke-report';
    report.textContent = payload;
    document.body.appendChild(report);
    try {
      await fetch('/smoke-result', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: payload
      });
    } catch (error) {
      result.unhandled.push(text(error));
    }
  }, { once: true });
})();`;

const REACT_SMOKE_SCRIPT_PATH = 'app/react-smoke-bootstrap.js';
let smokeResultBody = null;

const server = http.createServer((req, res) => {
  const requestUrl = new URL(req.url, 'http://localhost');
  const urlPath = decodeURIComponent(requestUrl.pathname);
  const relative = urlPath === '/' ? 'app/index.html' : urlPath.replace(/^\/+/, '');
  if (relative === 'smoke-result') {
    if (req.method === 'POST') {
      const chunks = [];
      req.on('data', (chunk) => chunks.push(chunk));
      req.on('end', () => {
        smokeResultBody = Buffer.concat(chunks).toString('utf8');
        res.writeHead(204, { 'cache-control': 'no-store' }).end();
      });
      return;
    }
    if (smokeResultBody == null) {
      res.writeHead(404, {
        'content-type': 'text/plain; charset=utf-8',
        'cache-control': 'no-store'
      }).end('pending');
      return;
    }
    res.writeHead(200, {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': 'no-store'
    }).end(smokeResultBody);
    return;
  }
  if (relative === REACT_SMOKE_SCRIPT_PATH) {
    res.writeHead(200, {
      'content-type': MIME['.js'],
      'cache-control': 'no-store'
    }).end(REACT_SMOKE_BOOTSTRAP);
    return;
  }
  const file = path.join(rootDir, relative);
  if (!file.startsWith(rootDir)) {
    res.writeHead(403).end();
    return;
  }
  fs.readFile(file, (error, data) => {
    if (error) {
      res.writeHead(404, { 'content-type': 'text/plain' }).end('404 ' + relative);
      return;
    }
    const ext = path.extname(file).toLowerCase();
    let body = data;
    if (relative === 'app/index.html' && requestUrl.searchParams.get('smoke') === 'react') {
      const smokeScript = '<script src="/app/react-smoke-bootstrap.js"></script>';
      const html = data.toString('utf8').replace('</head>', smokeScript + '\n</head>');
      body = Buffer.from(html);
    }
    res.writeHead(200, { 'content-type': MIME[ext] || 'application/octet-stream' }).end(body);
  });
});

server.listen(port, () => {
  console.log('[smoke] serving ' + rootDir + ' at http://localhost:' + port + '/app/index.html');
});
