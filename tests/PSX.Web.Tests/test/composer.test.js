import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';
import { createAgentManager, installAgentRuntime } from './agentHarness.js';

describe('Agent composer and slash commands', () => {
  let manager;
  let postedMessages;

  beforeEach(() => {
    const runtime = installAgentRuntime();
    postedMessages = runtime.postedMessages;
    manager = createAgentManager(runtime.AgentThreadManager);
  });

  it('accepts built-in and advertised ACP commands and canonicalizes their names', () => {
    manager.agentCommandsReady = true;
    manager.agentCommands = [{ name: '/review', description: 'Review' }];

    assert.deepEqual(
      JSON.parse(JSON.stringify(manager._validateSubmissionCommand('/NEW', []))),
      { allowed: true, text: '/new', psxCommand: 'new' }
    );
    assert.equal(manager._validateSubmissionCommand('/REVIEW src', []).text, '/review src');
  });

  it('rejects unknown leading commands and commands combined with attachments', () => {
    manager.agentCommandsReady = true;

    assert.deepEqual(JSON.parse(JSON.stringify(manager._validateSubmissionCommand('/unknown', []))), {
      allowed: false,
      command: '/unknown',
      reason: 'unsupported'
    });
    assert.equal(manager._validateSubmissionCommand('/new', [{ id: 'image-1' }]).reason, 'attachments_not_allowed');
    assert.equal(manager._validateSubmissionCommand('Explain /unknown here', []).allowed, true);
  });

  it('submits normal text and clears the draft only after validation succeeds', () => {
    manager.input.value = '  hello agent  ';
    manager._submit();

    assert.deepEqual(postedMessages.at(-1), { type: 'agent_submit', text: 'hello agent', attachments: [] });
    assert.equal(manager.input.value, '');
    assert.equal(manager.lastSubmittedDraft.text, 'hello agent');
  });

  it('turns submission into Stop while a run is busy', () => {
    manager.isBusy = true;
    manager.input.value = 'must not send';
    manager._submit();

    assert.deepEqual(postedMessages.at(-1), { type: 'agent_command', command: 'stop', value: '', requestId: '' });
    assert.equal(manager.input.value, 'must not send');
  });
});
