import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { describe, it } from 'node:test';
import { installAgentRuntime } from './agentHarness.js';
import { repositoryRoot } from './productionBundle.js';

describe('Agent workspace views', () => {
  it('keeps independent DOM and routes events only to the matching workspace', () => {
    const runtime = installAgentRuntime();
    const index = fs.readFileSync(path.join(repositoryRoot, 'wwwroot/index.html'), 'utf8');
    const templateMarkup = index.match(/<template id="agent-workspace-template">[\s\S]*?<\/template>/)?.[0];
    assert.ok(templateMarkup);
    document.body.innerHTML = `<div id="agents"></div>${templateMarkup}`;

    const terminal = {
      visible: true,
      setViewVisible(value) { this.visible = value; }
    };
    const views = new runtime.WorkspaceViewManager(
      terminal,
      document.getElementById('agents'),
      document.getElementById('agent-workspace-template')
    );
    const firstId = '11111111-1111-4111-8111-111111111111';
    const secondId = '22222222-2222-4222-8222-222222222222';

    views.createAgent({ workspaceId: firstId });
    views.createAgent({ workspaceId: secondId });
    views.agents.get(firstId).manager.input.value = 'first draft';
    views.agents.get(secondId).manager.input.value = 'second draft';

    views.handleAgentEvent({
      type: 'agent_state',
      workspaceId: firstId,
      status: 'running',
      busy: true,
      cwd: 'D:/first',
      title: 'First'
    });
    views.activate({ workspaceId: secondId, kind: 'agent' });

    assert.equal(views.agents.get(firstId).manager.meta.cwd.textContent, 'D:/first');
    assert.notEqual(views.agents.get(secondId).manager.meta.cwd.textContent, 'D:/first');
    assert.equal(views.agents.get(firstId).manager.input.value, 'first draft');
    assert.equal(views.agents.get(secondId).manager.input.value, 'second draft');
    assert.equal(views.agents.get(firstId).panel.hidden, true);
    assert.equal(views.agents.get(secondId).panel.hidden, false);
    assert.equal(terminal.visible, false);
  });

  it('removes a closed workspace and ignores its later events', () => {
    const runtime = installAgentRuntime();
    const index = fs.readFileSync(path.join(repositoryRoot, 'wwwroot/index.html'), 'utf8');
    const templateMarkup = index.match(/<template id="agent-workspace-template">[\s\S]*?<\/template>/)?.[0];
    document.body.innerHTML = `<div id="agents"></div>${templateMarkup}`;
    const views = new runtime.WorkspaceViewManager(
      { setViewVisible() {} },
      document.getElementById('agents'),
      document.getElementById('agent-workspace-template')
    );
    const workspaceId = '33333333-3333-4333-8333-333333333333';

    views.createAgent({ workspaceId });
    views.closeAgent(workspaceId);
    views.handleAgentEvent({ type: 'assistant_delta', workspaceId, text: 'late' });

    assert.equal(views.agents.has(workspaceId), false);
    assert.equal(document.querySelector(`[data-workspace-id="${workspaceId}"]`), null);
  });

  it('keeps transcript-only workspaces read-only even when their runtime is ready', () => {
    const runtime = installAgentRuntime();
    const index = fs.readFileSync(path.join(repositoryRoot, 'wwwroot/index.html'), 'utf8');
    const templateMarkup = index.match(/<template id="agent-workspace-template">[\s\S]*?<\/template>/)?.[0];
    document.body.innerHTML = `<div id="agents"></div>${templateMarkup}`;
    const views = new runtime.WorkspaceViewManager(
      { setViewVisible() {} },
      document.getElementById('agents'),
      document.getElementById('agent-workspace-template')
    );
    const workspaceId = '44444444-4444-4444-8444-444444444444';

    views.createAgent({ workspaceId });
    views.handleAgentEvent({ type: 'runtime_status', workspaceId, state: 'ready' });
    views.handleAgentEvent({
      type: 'agent_state',
      workspaceId,
      status: 'transcript_only',
      cwd: 'C:/saved'
    });

    const manager = views.agents.get(workspaceId).manager;
    assert.equal(manager.input.disabled, true);
    assert.equal(manager.sendButton.disabled, true);
    assert.equal(manager.sendButton.textContent, 'Read only');
  });
});
