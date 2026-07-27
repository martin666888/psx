// reactComposer.test.js — the React composer islands (Group B, Station 4).
//
// In react mode three independent roots own the attachment strip pills, the
// attach action row and the command hint text; the textarea, keyboard/IME and
// MenuSelect popups are deliberate legacy regions and must keep working in
// react mode (submit / stop / slash full chain).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, appModule } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;

const WS = '99999999-9999-4999-8999-999999999999';

await appModule('composer/composerIsland.js');

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function imageFile(name = 'shot.png', type = 'image/png') {
  return new File([new Uint8Array([1, 2, 3])], name, { type });
}

// Drive the real file-input change path. The React input relies on delegated
// events, so the change event must bubble (the legacy listener ignores that).
function attach(panel, ...files) {
  const input = role(panel, 'attachment-input');
  Object.defineProperty(input, 'files', { configurable: true, value: files });
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

const tick = () => new Promise((resolve) => setTimeout(resolve, 5));

async function drainReaders() {
  for (let i = 0; i < 5; i++) await tick();
}

async function waitForUploadClientId(posted, sinceIndex) {
  for (let i = 0; i < 50; i++) {
    const message = posted.slice(sinceIndex).find((entry) => entry && typeof entry.clientId === 'string' && entry.clientId);
    if (message) return message.clientId;
    await tick();
  }
  throw new Error('attachment upload was never posted to the host');
}

async function actAndSettle(run, predicate) {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 100 && !predicate(); i++) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 5));
    });
  }
  assert.ok(predicate(), 'react composer island did not settle in time');
}

// Every DOM fact the legacy tile writer produces for the strip.
function stripSnapshot(panel) {
  const strip = role(panel, 'attachments-strip');
  return {
    hidden: strip.hidden,
    tiles: [...strip.querySelectorAll('.agent-attachment-shell')].map((shell) => {
      const tile = shell.querySelector('.agent-attachment-tile');
      return {
        className: tile.className,
        title: tile.getAttribute('title'),
        ariaLabel: tile.getAttribute('aria-label'),
        hasImg: !!tile.querySelector('img'),
        name: tile.querySelector('.agent-attachment-name').textContent,
        uploadingBadge: tile.querySelector('.agent-attachment-status')?.textContent ?? null,
        removeLabel: shell.querySelector('.agent-attachment-remove')?.getAttribute('aria-label') ?? null
      };
    })
  };
}

async function mountComposer(uiMode) {
  const { app, panelFor, runtime } = await mountAgentApp(uiMode === 'react' ? { uiMode: 'react' } : {});
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS), runtime };
}

test('React attachment strip renders DOM equivalent to legacy through the upload lifecycle', async () => {
  const legacySnapshots = [];
  {
    const { app, panel, runtime } = await mountComposer('legacy');
    const since = runtime.postedMessages.length;
    attach(panel, imageFile());
    legacySnapshots.push(stripSnapshot(panel)); // uploading pill
    const clientId = await waitForUploadClientId(runtime.postedMessages, since);
    app.handle({
      type: 'agent_attachment_uploaded',
      workspaceId: WS,
      clientId,
      attachment: { id: 'srv-1', url: 'https://host/srv-1.png', fileName: 'shot.png', mimeType: 'image/png' }
    });
    legacySnapshots.push(stripSnapshot(panel)); // uploaded pill
    panel.querySelector('.agent-attachment-remove').click();
    legacySnapshots.push(stripSnapshot(panel)); // removed
    await drainReaders();
  }

  const reactSnapshots = [];
  {
    const { app, panel, runtime } = await mountComposer('react');
    const since = runtime.postedMessages.length;
    await actAndSettle(
      () => attach(panel, imageFile()),
      () => role(panel, 'attachments-strip').querySelector('.agent-attachment-tile')
    );
    reactSnapshots.push(stripSnapshot(panel));
    const clientId = await waitForUploadClientId(runtime.postedMessages, since);
    await actAndSettle(
      () =>
        app.handle({
          type: 'agent_attachment_uploaded',
          workspaceId: WS,
          clientId,
          attachment: { id: 'srv-1', url: 'https://host/srv-1.png', fileName: 'shot.png', mimeType: 'image/png' }
        }),
      () => role(panel, 'attachments-strip').querySelector('.agent-attachment-uploaded')
    );
    reactSnapshots.push(stripSnapshot(panel));
    await act(async () => {
      panel.querySelector('.agent-attachment-remove').click();
    });
    reactSnapshots.push(stripSnapshot(panel));
    await drainReaders();
  }

  assert.deepEqual(reactSnapshots, legacySnapshots);
});

test('react mode: the attach button mirrors the legacy disabled/title state', async () => {
  const { app, panel } = await mountComposer('react');
  await actAndSettle(
    () => app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready', supportsImage: true }),
    () => role(panel, 'attach') && !role(panel, 'attach').disabled
  );
  assert.equal(role(panel, 'attach').title, 'Attach images');

  await act(async () => {
    app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready', supportsImage: false });
  });
  const attachButton = role(panel, 'attach');
  assert.equal(attachButton.disabled, true);
  assert.equal(attachButton.title, 'Current ACP Agent does not support image input');
});

test('react mode: the command hint shows a rejected slash command and clears on input', async () => {
  const { app, panel } = await mountComposer('react');
  await actAndSettle(
    () => app.handle({ type: 'agent_command_rejected', workspaceId: WS, command: '/nope', reason: 'unsupported' }),
    () => role(panel, 'command-hint').textContent.includes('/nope')
  );
  const hint = role(panel, 'command-hint');
  assert.equal(hint.hidden, false);
  assert.match(hint.textContent, /无法识别命令：\/nope/);

  await act(async () => {
    const input = role(panel, 'input');
    input.value = 'hello';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
  assert.equal(role(panel, 'command-hint').hidden, true);
});

test('react mode: submit and stop post the same commands as legacy (textarea stays legacy)', async () => {
  let legacySend, legacyStop;
  {
    const { app, panel, runtime } = await mountComposer('legacy');
    app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready', busy: false });
    role(panel, 'input').value = 'ship it';
    role(panel, 'send').click();
    legacySend = runtime.postedMessages.at(-1);
    assert.equal(legacySend.text, 'ship it');
    app.handle({ type: 'agent_state', workspaceId: WS, status: 'busy', busy: true });
    role(panel, 'send').click();
    legacyStop = runtime.postedMessages.at(-1);
    assert.equal(legacyStop.command, 'stop');
  }

  const { app, panel, runtime } = await mountComposer('react');
  await actAndSettle(
    () => app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready', busy: false }),
    () => role(panel, 'attach')
  );
  role(panel, 'input').value = 'ship it';
  await act(async () => {
    role(panel, 'send').click();
  });
  assert.deepEqual(runtime.postedMessages.at(-1), legacySend, 'react posts the byte-identical send payload');
  await act(async () => {
    app.handle({ type: 'agent_state', workspaceId: WS, status: 'busy', busy: true });
    role(panel, 'send').click();
  });
  assert.deepEqual(runtime.postedMessages.at(-1), legacyStop, 'react posts the byte-identical stop command');
});
