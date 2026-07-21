import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const workspaceId = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function imageFile(name = 'shot.png', type = 'image/png') {
  return new File([new Uint8Array([1, 2, 3])], name, { type });
}

// Drive the composer's real file-input change path (the only attachment entry
// point without a runtime/support guard) so the app runs handleAttachmentFiles
// exactly as the shipped composer does.
function attach(panel, ...files) {
  const input = role(panel, 'attachment-input');
  Object.defineProperty(input, 'files', { configurable: true, value: files });
  input.dispatchEvent(new Event('change'));
}

const tick = () => new Promise((resolve) => setTimeout(resolve, 5));

// Flush any pending FileReader (started by uploadAttachmentFile) while this
// test's jsdom globals are still active, so a late onload never posts into the
// next test's freshly mounted window.
async function drainReaders() {
  for (let i = 0; i < 5; i++) await tick();
}

// The upload payload the composer posts after FileReader resolves carries the
// generated clientId the host echoes back on confirm/fail. Search only messages
// posted after the attach so we never pick up a neighbouring test's upload.
async function waitForUploadClientId(posted, sinceIndex) {
  for (let i = 0; i < 50; i++) {
    const message = posted.slice(sinceIndex).find((entry) => entry && typeof entry.clientId === 'string' && entry.clientId);
    if (message) return message.clientId;
    await tick();
  }
  throw new Error('attachment upload was never posted to the host');
}

describe('Attachment lifecycle', () => {
  async function mount() {
    const { app, panelFor, runtime } = await mountAgentApp();
    createAgentWorkspace(app, workspaceId);
    return { app, panel: panelFor(workspaceId), runtime };
  }

  it('renders a pending tile and tracks a local preview object URL', async () => {
    const { panel, runtime } = await mount();
    attach(panel, imageFile());

    const strip = role(panel, 'attachments-strip');
    assert.equal(strip.hidden, false);
    assert.equal(strip.querySelectorAll('.agent-attachment-tile').length, 1);
    assert.equal(strip.querySelector('.agent-attachment-uploading') != null, true);
    assert.equal(runtime.createdObjectUrls.length, 1);
    await drainReaders();
  });

  it('marks the attachment uploaded and revokes the local preview URL on confirmation', async () => {
    const { app, panel, runtime } = await mount();
    const since = runtime.postedMessages.length;
    attach(panel, imageFile());
    const clientId = await waitForUploadClientId(runtime.postedMessages, since);

    app.handle({
      type: 'agent_attachment_uploaded',
      workspaceId,
      clientId,
      attachment: { id: 'srv-1', url: 'https://host/srv-1.png', fileName: 'shot.png', mimeType: 'image/png' }
    });

    const strip = role(panel, 'attachments-strip');
    assert.ok(strip.querySelector('.agent-attachment-uploaded'));
    assert.equal(strip.querySelector('.agent-attachment-uploading'), null);
    assert.equal(runtime.revokedObjectUrls.length, 1);
  });

  it('revokes the object URL when a pending attachment is removed before upload', async () => {
    const { panel, runtime } = await mount();
    attach(panel, imageFile());
    const before = runtime.revokedObjectUrls.length;

    const strip = role(panel, 'attachments-strip');
    strip.querySelector('.agent-attachment-remove').click();

    assert.equal(strip.querySelectorAll('.agent-attachment-tile').length, 0);
    assert.equal(runtime.revokedObjectUrls.length, before + 1);
    await drainReaders();
  });

  it('reports upload failures as a system message and flags the tile', async () => {
    const { app, panel, runtime } = await mount();
    const since = runtime.postedMessages.length;
    attach(panel, imageFile());
    const clientId = await waitForUploadClientId(runtime.postedMessages, since);

    app.handle({ type: 'agent_attachment_failed', workspaceId, clientId, text: 'Upload rejected.' });

    const strip = role(panel, 'attachments-strip');
    assert.ok(strip.querySelector('.agent-attachment-failed'));
    assert.match(role(panel, 'thread').querySelector('.agent-system').textContent, /Upload rejected/);
  });

  it('refuses attachments when the agent does not support image input', async () => {
    const { app, panel } = await mount();
    app.handle({ type: 'agent_state', workspaceId, status: 'ready', supportsImage: false });
    attach(panel, imageFile());

    assert.equal(role(panel, 'attachments-strip').querySelectorAll('.agent-attachment-tile').length, 0);
    assert.match(role(panel, 'thread').querySelector('.agent-system').textContent, /does not support image input/);
  });
});

describe('Runtime install card', () => {
  async function mount() {
    const { app, panelFor, runtime } = await mountAgentApp();
    createAgentWorkspace(app, workspaceId, { ready: false });
    return { app, panel: panelFor(workspaceId), posted: runtime.postedMessages };
  }

  it('shows the install card and blocks input while the runtime is missing', async () => {
    const { app, panel } = await mount();
    app.handle({ type: 'runtime_status', workspaceId, state: 'missing', message: 'Not installed', canInstall: true, canCancel: false });

    const card = role(panel, 'runtime-card');
    assert.equal(card.hidden, false);
    assert.equal(card.dataset.state, 'missing');
    assert.equal(role(panel, 'runtime-install').hidden, false);
    assert.equal(role(panel, 'runtime-title').textContent, 'Agent runtime required');
    assert.equal(role(panel, 'input').disabled, true);
  });

  it('marks the card busy while installing and exposes cancel', async () => {
    const { app, panel } = await mount();
    app.handle({ type: 'runtime_status', workspaceId, state: 'installing', message: 'Installing', canInstall: false, canCancel: true });

    assert.equal(role(panel, 'runtime-card').getAttribute('aria-busy'), 'true');
    assert.equal(role(panel, 'runtime-cancel').hidden, false);
  });

  it('sends install and hides the card once the runtime is ready', async () => {
    const { app, panel, posted } = await mount();
    app.handle({ type: 'runtime_status', workspaceId, state: 'missing', canInstall: true, canCancel: false });
    role(panel, 'runtime-install').click();
    assert.equal(posted.at(-1).command, 'install_runtime');

    app.handle({ type: 'runtime_status', workspaceId, state: 'ready', canInstall: false, canCancel: false });
    assert.equal(role(panel, 'runtime-card').hidden, true);
    assert.equal(role(panel, 'input').disabled, false);
  });
});

describe('Modes and config options', () => {
  async function mount() {
    const { app, panelFor, runtime } = await mountAgentApp();
    createAgentWorkspace(app, workspaceId);
    return { app, panel: panelFor(workspaceId), posted: runtime.postedMessages };
  }

  it('populates the mode select and submits set_mode on change', async () => {
    const { app, panel, posted } = await mount();
    app.handle({ type: 'agent_modes', workspaceId, modes: [{ id: 'code', name: 'Code' }, { id: 'plan', name: 'Plan' }], currentModeId: 'plan' });

    const mode = role(panel, 'mode');
    assert.deepEqual([...mode.querySelectorAll('option')].map((option) => option.value), ['code', 'plan']);
    assert.equal(mode.value, 'plan');

    mode.value = 'code';
    mode.dispatchEvent(new Event('change'));
    assert.equal(posted.at(-1).command, 'set_mode');
    assert.equal(posted.at(-1).value, 'code');
  });

  it('renders config selects and submits set_config_option on change', async () => {
    const { app, panel, posted } = await mount();
    app.handle({
      type: 'agent_config_options',
      workspaceId,
      options: [{ id: 'model', name: 'Model', type: 'select', options: [{ value: 'a', name: 'A' }, { value: 'b', name: 'B' }], currentValue: 'b' }]
    });

    const select = role(panel, 'config-options').querySelector('select[data-config-id="model"]');
    assert.ok(select);
    assert.equal(select.value, 'b');

    select.value = 'a';
    select.dispatchEvent(new Event('change'));
    assert.equal(posted.at(-1).command, 'set_config_option');
    assert.equal(posted.at(-1).value, 'a');
  });
});

describe('History error and invalidation', () => {
  async function mount() {
    const { app, panelFor, runtime } = await mountAgentApp();
    createAgentWorkspace(app, workspaceId);
    return { app, panel: panelFor(workspaceId), posted: runtime.postedMessages };
  }

  it('renders a retryable error and re-requests history when Retry is clicked', async () => {
    const { app, panel, posted } = await mount();
    app.handle({ type: 'agent_history_error', workspaceId, text: 'History unavailable.' });

    assert.equal(role(panel, 'history-panel').hidden, false);
    const error = role(panel, 'history-panel').querySelector('.agent-history-error');
    assert.match(error.textContent, /History unavailable/);

    error.querySelector('.agent-history-retry').click();
    assert.equal(posted.at(-1).command, 'history');
  });

  it('refreshes on invalidation only while the History tab is active', async () => {
    const { app, panel, posted } = await mount();
    const before = posted.length;
    app.handle({ type: 'agent_history_invalidated', workspaceId });
    assert.equal(posted.length, before);

    role(panel, 'history-tab').click();
    app.handle({ type: 'agent_history_invalidated', workspaceId });
    assert.equal(posted.at(-1).command, 'history');
  });

  it('renders the thread list and switches to History on agent_threads', async () => {
    const { app, panel } = await mount();
    app.handle({ type: 'agent_threads', workspaceId, threads: [{ threadId: 't1', title: 'One', cwd: 'D:/one' }] });

    assert.equal(role(panel, 'history-panel').hidden, false);
    assert.equal(role(panel, 'history-panel').querySelectorAll('.agent-history-item').length, 1);
  });
});
