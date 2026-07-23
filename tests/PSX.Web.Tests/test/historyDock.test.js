import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  installAgentRuntime,
  installBreakpoint,
  agentTemplateMarkup,
  createAgentWorkspace,
  appModule
} from './agentHarness.js';

// The global History dock: one singleton outside every workspace panel,
// rendering the AgentHistoryStore (grouping/search/filter/open markers) and
// driving every `history` load through the AgentHistoryRequestBroker.

const {
  normalizeCwdKey,
  historyGroupName,
  buildHistoryGroups,
  filterHistoryGroups,
  formatHistoryTime,
  providerFilterOptions
} = await appModule('history/historyModel.js');

const WS = '11111111-1111-4111-8111-111111111111';
const OTHER = '22222222-2222-4222-8222-222222222222';

const tick = () => new Promise((resolve) => setTimeout(resolve, 5));

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// The dock's open/close control lives in every workspace toolbar.
function historyToggle(panel) {
  return role(panel, 'history-toggle');
}

function dock() {
  return document.querySelector('[data-role="history-dock"]');
}

function dockRole(name) {
  return dock().querySelector('[data-role="' + name + '"]');
}

// Mount the compiled Agent app like production main.js, but set the persisted
// dock state BEFORE createAgentApp reads it (each mount installs a fresh jsdom,
// so localStorage starts clean).
async function mountApp({ dockOpen = false, dockWidth = '', wide = false } = {}) {
  const runtime = installAgentRuntime();
  const breakpoint = installBreakpoint(wide);
  document.body.innerHTML = `<div id="agents"></div>${agentTemplateMarkup()}`;
  if (dockOpen) window.localStorage.setItem('psx.agent.historyDockOpen', '1');
  if (dockWidth) window.localStorage.setItem('psx.agent.historyDockWidth', dockWidth);
  const { createAgentApp } = await appModule('entry.js');
  const terminal = {
    visible: true,
    setViewVisible(value) {
      this.visible = value;
    }
  };
  const app = createAgentApp({
    terminalManager: terminal,
    container: document.getElementById('agents'),
    template: document.getElementById('agent-workspace-template')
  });
  return {
    app,
    runtime,
    terminal,
    breakpoint,
    posted: runtime.postedMessages,
    panelFor: (workspaceId) => document.querySelector(`.agent-panel[data-workspace-id="${workspaceId}"]`)
  };
}

// Mount with the dock open and one workspace; the dock's auto-load kicks the
// first broker request as soon as the workspace exists.
async function mountLoaded() {
  const mounted = await mountApp({ dockOpen: true, wide: true });
  createAgentWorkspace(mounted.app, WS);
  return { ...mounted, panel: mounted.panelFor(WS) };
}

// Answer the auto-load so the broker is idle for the next assertion.
function drainInitialLoad(app, threads = []) {
  app.handle({ type: 'agent_threads', workspaceId: WS, threads });
}

function thread(overrides) {
  // Payloads flowing through the broker use `provider`; the pure view model
  // consumes the normalized `providerKey`. Emit both, defaulting to the same
  // value, so one fixture serves both layers.
  const provider = overrides?.provider ?? overrides?.providerKey ?? 'claude-code';
  return {
    threadId: 't',
    title: 'Chat',
    cwd: 'D:/proj',
    updatedAt: '2026-07-20 10:00:00Z',
    sessionId: '',
    provider,
    providerKey: provider,
    ...overrides
  };
}

// --- pure grouping model -------------------------------------------------------

test('history model: timestamps format as same-day time, same-year date, or full date', () => {
  const now = new Date(2026, 6, 22, 12, 0, 0); // local 2026-07-22 12:00
  assert.equal(formatHistoryTime('', now), '');
  assert.equal(formatHistoryTime('not-a-date', now), 'not-a-date', 'unparseable stays raw');
  // Same local day -> HH:mm. The "u" string is UTC; compute the expected local
  // rendering instead of hardcoding a timezone-dependent wall clock.
  const sameDay = new Date(2026, 6, 22, 8, 5, 0);
  const sameDayUtc = sameDay.getFullYear() + '-' + String(sameDay.getMonth() + 1).padStart(2, '0') +
    '-' + String(sameDay.getDate()).padStart(2, '0') + ' ' +
    String(sameDay.getUTCHours()).padStart(2, '0') + ':' +
    String(sameDay.getUTCMinutes()).padStart(2, '0') + ':00Z';
  const expected = String(sameDay.getHours()).padStart(2, '0') + ':' + String(sameDay.getMinutes()).padStart(2, '0');
  assert.equal(formatHistoryTime(sameDayUtc, now), expected);
  // Same year, another day -> MM-dd.
  const march = new Date(2026, 2, 9, 8, 5, 0);
  const marchUtc = '2026-03-09 ' + String(march.getUTCHours()).padStart(2, '0') + ':05:00Z';
  assert.equal(formatHistoryTime(marchUtc, now), '03-09');
  // Another year -> yyyy-MM-dd (date may shift by one across timezones).
  assert.match(formatHistoryTime('2019-01-02 03:04:05Z', now), /^2019-01-0[12]$/);
});

test('history model: Windows cwd variants group together and roots survive normalization', () => {
  const key = normalizeCwdKey('C:\\Foo');
  assert.equal(normalizeCwdKey('c:\\foo\\'), key);
  assert.equal(normalizeCwdKey('C:/FOO'), key);
  // Drive and UNC roots are never stripped below the root.
  assert.equal(normalizeCwdKey('C:\\'), 'c:\\');
  assert.equal(normalizeCwdKey('c:/'), 'c:\\');
  assert.equal(normalizeCwdKey('\\\\server\\share\\'), '\\\\server\\share');
  assert.equal(normalizeCwdKey('//server/share/'), '\\\\server\\share');
  assert.equal(normalizeCwdKey(''), '');
  assert.equal(normalizeCwdKey('   '), '');
});

test('history model: group names use the last path segment with an unknown-workspace fallback', () => {
  assert.equal(historyGroupName('D:\\proj\\app'), 'app');
  assert.equal(historyGroupName('D:/proj/app/'), 'app');
  assert.equal(historyGroupName('C:\\'), 'C:');
  assert.equal(historyGroupName('\\\\server\\share'), 'share');
  assert.equal(historyGroupName(''), 'Unknown workspace');
});

test('history model: groups sort by latest activity, threads by updatedAt, unknown cwd groups separately', () => {
  const groups = buildHistoryGroups([
    thread({ threadId: 'old', cwd: 'D:/a', updatedAt: '2026-07-18 10:00:00Z' }),
    thread({ threadId: 'new-b', cwd: 'D:/b', updatedAt: '2026-07-21 10:00:00Z' }),
    thread({ threadId: 'new-b-2', cwd: 'd:\\b\\', updatedAt: '2026-07-20 10:00:00Z' }),
    thread({ threadId: 'no-cwd', cwd: '', updatedAt: '2026-07-22 10:00:00Z' })
  ], []);

  assert.deepEqual(groups.map((group) => group.key), ['', 'd:\\b', 'd:\\a']);
  assert.equal(groups[0].name, 'Unknown workspace');
  assert.equal(groups[1].name, 'b');
  assert.equal(groups[1].path, 'D:/b', 'display path comes from the latest thread');
  assert.deepEqual(groups[1].threads.map((t) => t.threadId), ['new-b', 'new-b-2']);
});

test('history model: search matches title, path and provider, filter narrows by provider key', () => {
  const providers = [{ key: 'claude-code', displayName: 'Claude Code', assistantName: 'Claude', isDefault: true }];
  const groups = buildHistoryGroups([
    thread({ threadId: 't1', title: 'Refactor parser', provider: 'claude-code' }),
    thread({ threadId: 't2', title: 'Fix bug', cwd: 'D:/other', provider: 'mystery' })
  ], providers);

  const flat = groups.flatMap((g) => g.threads);
  assert.equal(flat.find((t) => t.threadId === 't1').providerDisplay, 'Claude Code');
  assert.equal(flat.find((t) => t.threadId === 't2').providerDisplay, 'mystery', 'unknown provider shows the raw key');

  const ids = (query, providerKey) => filterHistoryGroups(groups, query, providerKey).flatMap((g) => g.threads.map((t) => t.threadId));
  assert.deepEqual(ids('parser', ''), ['t1']);
  assert.deepEqual(ids('other', ''), ['t2']);
  assert.deepEqual(ids('claude code', ''), ['t1']);
  assert.deepEqual(ids('', 'mystery'), ['t2']);
  assert.equal(filterHistoryGroups(groups, 'nothing matches', '').length, 0);
});

test('history model: filter options come from the catalog plus unknown keys seen in data', () => {
  const options = providerFilterOptions(
    [thread({ provider: 'claude-code' }), thread({ provider: 'mystery' })],
    [{ key: 'claude-code', displayName: 'Claude Code', assistantName: 'Claude', isDefault: true }]
  );
  assert.deepEqual(options, [
    { value: 'claude-code', label: 'Claude Code' },
    { value: 'mystery', label: 'mystery' }
  ]);
});

// --- dock shell ------------------------------------------------------------------

test('dock: a newly created workspace toolbar toggle inherits the dock open state', async () => {
  const { app, panelFor } = await mountApp({ dockOpen: true, wide: true });
  createAgentWorkspace(app, WS);
  assert.equal(historyToggle(panelFor(WS)).getAttribute('aria-expanded'), 'true');

  // Create another workspace while the dock is still open: its toolbar toggle
  // must also start expanded, not with the template's default "false".
  createAgentWorkspace(app, OTHER);
  assert.equal(historyToggle(panelFor(OTHER)).getAttribute('aria-expanded'), 'true',
    'new tab toggle syncs with the already-open dock');
});

test('dock: exactly one dock exists across workspaces and hides for terminal workspaces', async () => {
  const { app, panelFor } = await mountApp({ dockOpen: true, wide: true });
  createAgentWorkspace(app, WS);
  createAgentWorkspace(app, OTHER);
  assert.equal(document.querySelectorAll('[data-role="history-dock"]').length, 1);
  assert.equal(dock().hidden, false);

  app.handle({ type: 'workspace_activated', workspaceId: 'term-1', kind: 'terminal' });
  assert.equal(dock().hidden, true, 'terminal view hides the whole agent area');

  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  assert.equal(dock().hidden, false);
  assert.ok(panelFor(WS));
});

test('dock: narrow open/collapse is temporary and restores the desktop preference', async () => {
  const { app, panelFor, breakpoint } = await mountApp();
  createAgentWorkspace(app, WS);
  assert.equal(dock().hidden, true, 'first run is temporarily collapsed in narrow mode');

  // /history opens only the narrow override; it does not overwrite the
  // desktop preference stored in localStorage.
  const panel = panelFor(WS);
  role(panel, 'input').value = '/history';
  role(panel, 'input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
  assert.equal(dock().hidden, false);
  assert.equal(window.localStorage.getItem('psx.agent.historyDockOpen'), null);

  historyToggle(panel).click();
  assert.equal(dock().hidden, true);
  assert.equal(window.localStorage.getItem('psx.agent.historyDockOpen'), null);

  breakpoint.setWide(true);
  assert.equal(dock().hidden, false, 'wide mode restores the first-run preference');
});

test('dock: defaults to 280px and restores the persisted width', async () => {
  const fresh = await mountApp();
  // The width variable lives on the shared container so the canvas offset
  // rules (siblings of the dock) inherit the dragged width too.
  assert.equal(document.getElementById('agents').style.getPropertyValue('--agent-history-width'), '280px');
  assert.equal(dock().style.getPropertyValue('--agent-history-width'), '', 'never on the dock element itself');

  const restored = await mountApp({ dockWidth: '352' });
  assert.equal(document.getElementById('agents').style.getPropertyValue('--agent-history-width'), '352px');
  assert.ok(restored.app);
});

test('dock: without an Agent workspace it asks for one and sends no request', async () => {
  const { posted } = await mountApp({ dockOpen: true, wide: true });
  assert.match(dockRole('history-content').textContent, /Open an Agent workspace to load history/);
  assert.equal(posted.filter((entry) => entry.command === 'history').length, 0);
});

// --- dock data flow ----------------------------------------------------------------

test('dock: loads through the broker when a workspace exists and renders groups', async () => {
  const { app, posted } = await mountLoaded();
  const historyRequests = posted.filter((entry) => entry.command === 'history');
  assert.equal(historyRequests.length, 1);
  assert.equal(historyRequests[0].workspaceId, WS);

  drainInitialLoad(app, [
    thread({ threadId: 't1', title: 'Alpha', cwd: 'C:\\Foo', updatedAt: '2026-07-21 10:00:00Z' }),
    thread({ threadId: 't2', title: 'Beta', cwd: 'c:/foo/', updatedAt: '2026-07-20 10:00:00Z' }),
    thread({ threadId: 't3', title: 'Gamma', cwd: '', updatedAt: '2026-07-19 10:00:00Z', provider: 'mystery' })
  ]);

  const headings = [...dock().querySelectorAll('.agent-history-group-label strong')].map((node) => node.textContent);
  assert.deepEqual(headings, ['Foo', 'Unknown workspace']);
  assert.equal(dock().querySelectorAll('.agent-history-item').length, 3);
});

test('dock: search box and provider filter narrow the rendered list', async () => {
  const { app } = await mountLoaded();
  app.handle({ type: 'agent_providers', providers: [{ key: 'claude-code', displayName: 'Claude Code', assistantName: 'Claude', isDefault: true }] });
  drainInitialLoad(app, [
    thread({ threadId: 't1', title: 'Alpha', provider: 'claude-code' }),
    thread({ threadId: 't2', title: 'Beta', cwd: 'D:/other', provider: 'mystery' })
  ]);
  assert.equal(dock().querySelectorAll('.agent-history-item').length, 2);
  assert.match(dock().textContent, /Claude Code/);
  assert.match(dock().textContent, /mystery/);

  const search = dockRole('history-search');
  search.value = 'alpha';
  search.dispatchEvent(new Event('input'));
  assert.equal(dock().querySelectorAll('.agent-history-item').length, 1);

  search.value = '';
  search.dispatchEvent(new Event('input'));
  const filter = dockRole('history-provider-filter');
  filter.value = 'mystery';
  filter.dispatchEvent(new Event('change'));
  const titles = [...dock().querySelectorAll('.agent-history-item strong')].map((node) => node.textContent);
  assert.deepEqual(titles, ['Beta']);
});

test('dock: marks the active workspace thread Current and threads open elsewhere Open', async () => {
  const { app } = await mountLoaded();
  createAgentWorkspace(app, OTHER);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  app.handle({ type: 'agent_state', workspaceId: WS, threadId: 't1', status: 'ready' });
  app.handle({ type: 'agent_state', workspaceId: OTHER, threadId: 't2', status: 'ready' });
  drainInitialLoad(app, [thread({ threadId: 't1' }), thread({ threadId: 't2' }), thread({ threadId: 't3' })]);

  const rows = [...dock().querySelectorAll('.agent-history-item')];
  const byId = Object.fromEntries(rows.map((row) => [row.dataset.threadId, row]));
  assert.equal(byId.t1.getAttribute('aria-current'), 'true');
  assert.ok(byId.t1.querySelector('.agent-history-current'));
  assert.equal(byId.t2.getAttribute('aria-current'), 'false');
  assert.ok(byId.t2.querySelector('.agent-history-open'));
  assert.equal(byId.t3.querySelector('.agent-history-open'), null);
});

test('dock: rows carry the full provider and time in tooltip and accessible name', async () => {
  const { app } = await mountLoaded();
  app.handle({ type: 'agent_providers', providers: [{ key: 'claude-code', displayName: 'Claude Code', assistantName: 'Claude', isDefault: true }] });
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  app.handle({ type: 'agent_state', workspaceId: WS, threadId: 't1', status: 'ready' });
  drainInitialLoad(app, [thread({ threadId: 't1', title: 'Alpha' })]);

  const row = dock().querySelector('.agent-history-item');
  assert.match(row.title, /Claude Code \| /);
  assert.match(row.getAttribute('aria-label'), /^Alpha, Current, Claude Code \| /);
});

test('dock: clicking a thread sends load_thread through a broker channel', async () => {
  const { app, posted } = await mountLoaded();
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  drainInitialLoad(app, [thread({ threadId: 't-42' })]);

  dock().querySelector('.agent-history-item').click();
  const load = posted.at(-1);
  assert.equal(load.command, 'load_thread');
  assert.equal(load.value, 't-42');
  assert.equal(load.workspaceId, WS);
});

test('dock: renders a retryable error and re-requests history when Retry is clicked', async () => {
  const { app, posted } = await mountLoaded();
  app.handle({ type: 'agent_history_error', workspaceId: WS, text: 'History unavailable.' });

  const error = dockRole('history-content').querySelector('.agent-history-error');
  assert.match(error.textContent, /History unavailable/);

  const before = posted.filter((entry) => entry.command === 'history').length;
  error.querySelector('.agent-history-retry').click();
  assert.equal(posted.filter((entry) => entry.command === 'history').length, before + 1);
});

test('dock: thread-open failures keep the list and Retry reopens the failed thread', async () => {
  const { app, posted } = await mountLoaded();
  drainInitialLoad(app, [thread({ threadId: 't-42' })]);
  // Global by design: the source workspace may already be closed, so the
  // event carries no workspaceId and must still land in the dock (and must
  // never reach a workspace timeline).
  app.handle({
    type: 'agent_thread_open_error',
    threadId: 't-42',
    text: 'PSX could not open the selected Agent thread. Try again.'
  });

  const error = dockRole('history-content').querySelector('.agent-history-open-error');
  assert.match(error.textContent, /could not open the selected Agent thread/);
  assert.ok(dockRole('history-content').querySelector('.agent-history-item'), 'the cached list stays usable');
  assert.equal(document.querySelector('.agent-panel')?.textContent.includes('could not open') ?? false, false);

  error.querySelector('.agent-history-retry').click();
  const retry = posted.at(-1);
  assert.equal(retry.command, 'load_thread');
  assert.equal(retry.value, 't-42');
  assert.equal(dockRole('history-content').querySelector('.agent-history-open-error'), null);
});

test('dock: a new row selection dismisses a previous thread-open failure', async () => {
  const { app, posted } = await mountLoaded();
  drainInitialLoad(app, [
    thread({ threadId: 't-42' }),
    thread({ threadId: 't-43', title: 'Another thread' })
  ]);
  app.handle({
    type: 'agent_thread_open_error',
    threadId: 't-42',
    text: 'PSX could not open the selected Agent thread. Try again.'
  });

  dockRole('history-content').querySelector('[data-thread-id="t-43"]').click();

  assert.equal(posted.at(-1).value, 't-43');
  assert.equal(dockRole('history-content').querySelector('.agent-history-open-error'), null);
});

test('dock: invalidation refreshes once through the broker', async () => {
  const { app, posted } = await mountLoaded();
  drainInitialLoad(app);
  const before = posted.filter((entry) => entry.command === 'history').length;
  app.handle({ type: 'agent_history_invalidated', workspaceId: WS });
  app.handle({ type: 'agent_history_invalidated', workspaceId: WS });
  await tick();
  assert.equal(posted.filter((entry) => entry.command === 'history').length, before + 1);
});

// --- composer /history seam ---------------------------------------------------------

test('composer: /history from the input opens the dock without a prompt or busy state', async () => {
  const { app, panel, posted } = await mountLoaded();
  drainInitialLoad(app);
  app.handle({ type: 'agent_state', workspaceId: WS, status: 'running', busy: true });

  const before = posted.length;
  const threadContent = role(panel, 'thread').innerHTML;
  role(panel, 'input').value = '/history';
  role(panel, 'input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));

  const sent = posted.slice(before);
  assert.equal(sent.filter((entry) => entry.type === 'agent_message').length, 0, 'no prompt is sent');
  assert.equal(sent.filter((entry) => entry.command === 'stop').length, 0, 'busy is not interrupted');
  assert.equal(role(panel, 'thread').innerHTML, threadContent, 'no thread card is written');
  assert.equal(role(panel, 'input').value, '');
  assert.equal(dock().hidden, false);
  assert.ok(sent.some((entry) => entry.command === 'history'), 'the broker sends the history command');
});

test('composer: /history from the command menu reaches the same global seam', async () => {
  const { app, panel, posted } = await mountLoaded();
  drainInitialLoad(app);
  const before = posted.length;

  const input = role(panel, 'input');
  input.value = '/hist';
  input.dispatchEvent(new Event('input'));
  const menu = role(panel, 'command-menu');
  assert.equal(menu.hidden, false);
  const item = [...menu.querySelectorAll('.agent-command-item')].find((row) => row.textContent.includes('/history'));
  assert.ok(item, 'the PSX /history command is listed');
  item.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));

  const sent = posted.slice(before);
  assert.equal(dock().hidden, false);
  assert.equal(input.value, '');
  assert.equal(sent.filter((entry) => entry.type === 'agent_message').length, 0);
  assert.ok(sent.some((entry) => entry.command === 'history'));
});

test('composer: /history with arguments takes the same navigation seam, even while busy', async () => {
  const { app, panel, posted } = await mountLoaded();
  drainInitialLoad(app);
  app.handle({ type: 'agent_state', workspaceId: WS, status: 'running', busy: true });

  const before = posted.length;
  const threadContent = role(panel, 'thread').innerHTML;
  role(panel, 'input').value = '/history foo';
  role(panel, 'input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));

  const sent = posted.slice(before);
  assert.equal(sent.filter((entry) => entry.type === 'agent_message').length, 0, 'no prompt is sent');
  assert.equal(sent.filter((entry) => entry.command === 'stop').length, 0, 'busy is not interrupted');
  assert.equal(role(panel, 'thread').innerHTML, threadContent, 'no thread card is written');
  assert.equal(role(panel, 'input').value, '');
  assert.equal(dock().hidden, false);
  assert.ok(sent.some((entry) => entry.command === 'history'), 'the broker sends the history command');
});

test('dock: the toolbar toggle opens the dock and refreshes through the broker', async () => {
  const { app, panelFor, posted } = await mountApp();
  createAgentWorkspace(app, WS);
  assert.equal(dock().hidden, true);

  historyToggle(panelFor(WS)).click();
  assert.equal(dock().hidden, false);
  assert.ok(posted.some((entry) => entry.command === 'history'), 'toggle goes through the broker refresh');
});

test('dock: store-driven renders keep the scroll position, search and filter reset it', async () => {
  const { app } = await mountLoaded();
  drainInitialLoad(app, [thread({ threadId: 't1' }), thread({ threadId: 't2', cwd: 'D:/other' })]);

  const content = dockRole('history-content');
  content.scrollTop = 120;
  // A workspace event only refreshes open markers; the list must not jump.
  app.handle({ type: 'agent_state', workspaceId: WS, threadId: 't1', status: 'ready' });
  assert.equal(content.scrollTop, 120);

  const search = dockRole('history-search');
  search.value = 't';
  search.dispatchEvent(new Event('input'));
  assert.equal(content.scrollTop, 0, 'search resets the scroll position');

  content.scrollTop = 120;
  const filter = dockRole('history-provider-filter');
  filter.value = 'claude-code';
  filter.dispatchEvent(new Event('change'));
  assert.equal(content.scrollTop, 0, 'provider filter resets the scroll position');
});

test('dock: first open sends exactly one history request, later opens refresh once', async () => {
  const { app, panelFor, posted } = await mountApp();
  createAgentWorkspace(app, WS);
  const historyCommands = () => posted.filter((entry) => entry.command === 'history').length;
  const toggle = historyToggle(panelFor(WS));

  toggle.click();
  assert.equal(historyCommands(), 1, 'first open does not double-request');

  drainInitialLoad(app, [thread({ threadId: 't1' })]);
  await tick();
  assert.equal(historyCommands(), 1, 'no queued second wave after the landing');

  // Already loaded: opening again is an explicit single refresh.
  toggle.click();
  toggle.click();
  assert.equal(historyCommands(), 2);

  drainInitialLoad(app);
  await tick();
  assert.equal(historyCommands(), 2, 'still no queued follow-up wave');

  // The refresh button keeps its explicit single-refresh semantics.
  dockRole('history-refresh').click();
  assert.equal(historyCommands(), 3);
});

// --- folding, preview limit and active-session indicator -----------------------

function makeThreads(count, cwd) {
  return Array.from({ length: count }, (_, i) =>
    thread({ threadId: 't' + i, title: 'Task ' + i, cwd, updatedAt: '2026-07-2' + (i % 10) + ' 10:00:00Z' })
  );
}

test('dock: a group shows at most 5 threads with a Show-all link until expanded', async () => {
  const { app } = await mountLoaded();
  drainInitialLoad(app, makeThreads(7, 'C:\\Project'));
  let group = dock().querySelector('.agent-history-group');
  assert.equal(group.querySelectorAll('.agent-history-item').length, 5, 'preview limit');
  assert.ok(group.querySelector('.agent-history-show-more'));

  group.querySelector('.agent-history-show-more').click();
  group = dock().querySelector('.agent-history-group');
  assert.equal(group.querySelectorAll('.agent-history-item').length, 7, 'expanded shows all');
  assert.equal(group.querySelector('.agent-history-show-more'), null, 'link is gone');
});

test('dock: clicking the group header folds and unfolds the thread list', async () => {
  const { app } = await mountLoaded();
  drainInitialLoad(app, makeThreads(3, 'C:\\Project'));
  let group = dock().querySelector('.agent-history-group');
  let header = group.querySelector('.agent-history-group-header');
  assert.equal(group.dataset.folded, 'false');
  assert.equal(group.querySelector('.agent-history-group-threads').hidden, false);

  header.click();
  group = dock().querySelector('.agent-history-group');
  header = group.querySelector('.agent-history-group-header');
  assert.equal(group.dataset.folded, 'true', 'folded after click');
  assert.equal(group.querySelector('.agent-history-group-threads').hidden, true);
  assert.equal(header.getAttribute('aria-expanded'), 'false');

  header.click();
  group = dock().querySelector('.agent-history-group');
  assert.equal(group.dataset.folded, 'false', 'unfolded after second click');
  assert.equal(group.querySelector('.agent-history-group-threads').hidden, false);
});

test('dock: the group with the active session shows a marker dot even when folded', async () => {
  const { app, panelFor } = await mountLoaded();
  createAgentWorkspace(app, OTHER);
  app.handle({ type: 'workspace_activated', workspaceId: OTHER, kind: 'agent' });
  app.handle({ type: 'agent_state', workspaceId: OTHER, threadId: 't1', status: 'ready' });
  drainInitialLoad(app, makeThreads(3, 'C:\\Project'));

  let group = dock().querySelector('.agent-history-group');
  assert.ok(group.querySelector('.agent-history-group-active'), 'active dot present');

  group.querySelector('.agent-history-group-header').click();
  group = dock().querySelector('.agent-history-group');
  assert.equal(group.dataset.folded, 'true');
  assert.ok(group.querySelector('.agent-history-group-active'), 'dot survives folding');
});

test('dock: searching forces every group open past the fold and the preview limit', async () => {
  const { app } = await mountLoaded();
  drainInitialLoad(app, makeThreads(7, 'C:\\Project'));
  // Fold it first
  dock().querySelector('.agent-history-group-header').click();
  let group = dock().querySelector('.agent-history-group');
  assert.equal(group.dataset.folded, 'true');

  // A search must unfold and remove the preview cap.
  const search = dockRole('history-search');
  search.value = 'Task';
  search.dispatchEvent(new Event('input'));
  group = dock().querySelector('.agent-history-group');
  assert.equal(group.dataset.folded, 'false', 'search unfolds');
  assert.equal(group.querySelectorAll('.agent-history-item').length, 7, 'search bypasses preview limit');
  assert.equal(group.querySelector('.agent-history-show-more'), null, 'no link while searching');
});
