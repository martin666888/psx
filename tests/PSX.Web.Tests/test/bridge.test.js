import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { installAgentRuntime } from './agentHarness.js';

describe('WebView bridge', () => {
  it('sends permission and command payloads without changing ACP identifiers', () => {
    const { Bridge, postedMessages } = installAgentRuntime();

    Bridge.sendAgentPermissionResponse('request-9', 'bypassPermissions');
    Bridge.sendAgentCommand('set_config_option', 'plan', 'mode');
    Bridge.sendPasteRequest('session-1', 'paste-1');

    assert.deepEqual(postedMessages, [
      { type: 'agent_permission_response', requestId: 'request-9', value: 'bypassPermissions' },
      { type: 'agent_command', command: 'set_config_option', value: 'plan', requestId: 'mode' },
      { type: 'paste_request', sessionId: 'session-1', requestId: 'paste-1' }
    ]);
  });

  it('accepts host messages as JSON strings or objects and ignores malformed JSON', () => {
    const { Bridge, emitHostMessage } = installAgentRuntime();
    const messages = [];
    const errors = [];
    const originalError = console.error;
    console.error = (...args) => errors.push(args);
    Bridge.onHostMessage((message) => messages.push(message));

    emitHostMessage('{"type":"agent_state","busy":false}');
    emitHostMessage({ type: 'plan_update', entries: [] });
    emitHostMessage('{not-json');

    console.error = originalError;
    assert.deepEqual(JSON.parse(JSON.stringify(messages)), [
      { type: 'agent_state', busy: false },
      { type: 'plan_update', entries: [] }
    ]);
    assert.equal(errors.length, 1);
  });
});
