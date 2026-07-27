// smoke-server.mjs — serves an unpacked Release ZIP's wwwroot over local HTTP
// for the Release browser smoke. The Vite output under wwwroot/app uses
// absolute /app/ URLs, so a plain static server maps the WebView2 virtual-host
// layout one-to-one. `?smoke=react` injects an isolated host-bridge fixture
// into /app/index.html and writes its result into the DOM.
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
<script>
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
      postMessage() {},
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
    emit({ type: 'agent_workspace_created', workspaceId });
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
    emit({
      type: 'plan_update',
      workspaceId,
      runId: 'smoke-run',
      entries: [{ content: 'Verify release', status: 'in_progress' }]
    });

    const mounted = await waitFor(() => {
      const panel = document.querySelector('[data-workspace-id="' + workspaceId + '"]');
      return panel
        && panel.querySelector('[data-role="thread"]')?.dataset.islandState === 'mounted'
        && panel.querySelector('[data-role="session-meta-host"]')?.dataset.islandState === 'mounted'
        && panel.querySelector('[data-role="context-usage-host"]')?.dataset.islandState === 'mounted'
        && panel.querySelector('[data-role="runtime-host"]')?.dataset.islandState === 'mounted'
        && panel.querySelector('[data-role="plan-panel"]')?.dataset.islandState === 'mounted';
    });
    const panel = document.querySelector('[data-workspace-id="' + workspaceId + '"]');
    result.checks.islandsMounted = mounted;
    result.checks.realTimelineNode =
      panel?.querySelector('.agent-message-user')?.textContent.includes('React release smoke') === true;
    result.checks.realRuntimeNode =
      panel?.querySelector('[data-role="runtime-card"]')?.dataset.state === 'missing';

    // The packaged Vite output must serve React and the Agent app as hashed
    // dynamic chunks from /app/assets/.
    const resources = performance.getEntriesByType('resource').map((entry) => entry.name);
    result.checks.reactVendorChunkLoaded =
      resources.some((name) => /\/app\/assets\/react-vendor-[-\w]+\.js(?:$|\?)/.test(name));
    result.checks.agentChunkLoaded =
      resources.some((name) => /\/app\/assets\/entry-[-\w]+\.js(?:$|\?)/.test(name));

    const passed =
      Object.values(result.checks).every((value) => value === true)
      && result.errors.length === 0
      && result.warnings.length === 0
      && result.unhandled.length === 0;
    document.body.dataset.smokeStatus = passed ? 'pass' : 'fail';
    const report = document.createElement('pre');
    report.id = 'react-smoke-report';
    report.textContent = JSON.stringify(result);
    document.body.appendChild(report);
  }, { once: true });
})();
</script>`;

const server = http.createServer((req, res) => {
  const requestUrl = new URL(req.url, 'http://localhost');
  const urlPath = decodeURIComponent(requestUrl.pathname);
  const relative = urlPath === '/' ? 'app/index.html' : urlPath.replace(/^\/+/, '');
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
      const html = data.toString('utf8').replace('</head>', REACT_SMOKE_BOOTSTRAP + '\n</head>');
      body = Buffer.from(html);
    }
    res.writeHead(200, { 'content-type': MIME[ext] || 'application/octet-stream' }).end(body);
  });
});

server.listen(port, () => {
  console.log('[smoke] serving ' + rootDir + ' at http://localhost:' + port + '/app/index.html');
});
