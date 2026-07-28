// timelineReactTree.test.js — semantic contracts for the React timeline.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import { appModule, installAgentRuntime } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;

const NAME = 'Agent';
const { TimelineProjection } = await appModule('timeline/timelineViewModel.js');
const { mountTimelineIsland } = await appModule('timeline/timelineIsland.js');

const BASE_CALLBACKS = {
  copyText: async () => true,
  onOpenTerminal: () => {},
  onDecisionOption: () => {},
  onElicitationAction: () => {},
  createAttachmentTile: (attachment) => {
    const tile = document.createElement('div');
    tile.className = 'message-attachment-tile';
    tile.textContent = attachment.fileName || '';
    return tile;
  }
};

async function renderEvents(events, callbackOverrides = {}) {
  installAgentRuntime();
  const projection = new TimelineProjection();
  for (const [type, raw] of events) projection.apply(type, raw ?? {}, NAME);
  const host = document.createElement('div');
  document.body.appendChild(host);
  const failures = [];
  const island = mountTimelineIsland(host, (error, phase) => failures.push({ error, phase }));
  const callbacks = { ...BASE_CALLBACKS, ...callbackOverrides };
  const render = async () => {
    await act(async () => {
      island.render({
        rows: projection.snapshot().rows,
        assistantName: NAME,
        callbacks
      });
    });
  };
  await render();
  return {
    host,
    island,
    projection,
    callbacks,
    failures,
    render,
    async dispose() {
      await act(async () => island.dispose());
      host.remove();
    }
  };
}

const STREAMING = [
  ['user_message', { text: 'hello **md**', attachments: [{ fileName: 'shot.png' }] }],
  ['thinking_started', {}],
  ['thinking_delta', { text: 'pondering' }],
  ['thinking_finished', {}],
  ['tool_started', { runId: 'r1', toolCallId: 't1', name: 'Bash', summary: 'run ls', input: 'ls' }],
  ['tool_delta', { toolCallId: 't1', text: '\nout' }],
  ['tool_finished', { toolCallId: 't1', status: 'completed' }],
  ['assistant_delta', { text: 'Hi ' }],
  ['assistant_delta', { text: '**there**' }],
  ['run_finished', {}],
  ['command_result', { text: 'done' }]
];

test('streaming renders messages, thinking, tools, attachments and completion state', async () => {
  const view = await renderEvents(STREAMING);
  assert.equal(view.failures.length, 0);
  assert.equal(view.host.querySelectorAll('.agent-turn').length, 1);
  assert.match(view.host.querySelector('.agent-message-user').textContent, /hello md/);
  assert.equal(view.host.querySelector('.message-attachment-tile').textContent, 'shot.png');
  assert.equal(view.host.querySelector('.agent-thinking-block').getAttribute('aria-busy'), 'false');
  const toolCard = view.host.querySelector('.agent-tool-card');
  assert.equal(toolCard.dataset.state, 'done');
  assert.ok(toolCard.classList.contains('border-0'), 'grouped tool rows do not render divider borders');
  assert.ok(view.host.querySelector('.agent-run-group-body').classList.contains('gap-1'));
  assert.equal(view.host.querySelector('.agent-message-assistant .agent-message-body').dataset.raw, 'Hi **there**');
  assert.ok(view.host.querySelector('.agent-copy-button'));
  assert.match(view.host.textContent, /done/);
  await view.dispose();
});

test('history replay restores every persisted row kind', async () => {
  const view = await renderEvents([
    ['agent_thread_loaded', {
      clear: true,
      messages: [
        { role: 'user', text: 'hi' },
        { role: 'thinking', text: 'hmm' },
        { role: 'tool', runId: 'r1', toolCallId: 't1', summary: 'call', toolOutput: 'out', toolStatus: 'completed' },
        { role: 'assistant', text: 'answer' },
        { role: 'system', text: 'note' }
      ]
    }]
  ]);
  assert.match(view.host.textContent, /hi/);
  assert.match(view.host.textContent, /hmm/);
  assert.equal(view.host.querySelector('.agent-tool-card').dataset.state, 'done');
  assert.match(view.host.querySelector('.agent-message-assistant').textContent, /answer/);
  assert.match(view.host.querySelector('.agent-system').textContent, /note/);
  await view.dispose();
});

test('historical mode transitions map selected, cancelled and interrupted states', async () => {
  const messages = ['selected', 'cancelled', 'interrupted'].map((decisionState, index) => ({
    role: 'mode_transition',
    requestId: 'mt-' + decisionState,
    toolCallId: 'tc-' + index,
    name: 'Plan',
    text: '# Proposal',
    decisionOptions: [{ optionId: 'approve', name: 'Approve', kind: 'allow_once' }],
    selectedOptionId: decisionState === 'selected' ? 'approve' : '',
    decisionState
  }));
  const view = await renderEvents([
    ['agent_thread_loaded', { clear: true, messages }]
  ]);
  const cards = [...view.host.querySelectorAll('.agent-mode-transition')];
  assert.equal(cards.length, 3);
  assert.deepEqual(
    cards.map((card) => card.querySelector('.agent-mode-transition-header-state').textContent),
    ['Selected', 'Cancelled', 'Interrupted']
  );
  assert.equal(cards[0].querySelector('[data-option-id="approve"]').getAttribute('aria-pressed'), 'true');
  await view.dispose();
});

test('recovery and later events stay in one turn without duplicate-key warnings', async () => {
  const errors = [];
  const originalError = console.error;
  console.error = (...args) => errors.push(args.map(String).join(' '));
  try {
    const view = await renderEvents([
      ['user_message', { text: 'go' }],
      ['assistant_delta', { text: 'partial' }],
      ['resume_failed', { message: 'Could not resume', detail: 'boom' }],
      ['thinking_started', {}],
      ['thinking_delta', { text: 'recovering' }],
      ['run_finished', {}]
    ]);
    assert.equal(view.host.querySelectorAll('.agent-turn').length, 1);
    assert.ok(view.host.querySelector('.agent-recovery'));
    assert.match(view.host.textContent, /recovering/);
    await view.dispose();
  } finally {
    console.error = originalError;
  }
  assert.deepEqual(errors.filter((line) => /same key|unique/i.test(line)), []);
});

test('permission and question actions expose semantic state and callbacks', async () => {
  const chosen = [];
  const view = await renderEvents([
    ['user_message', { text: 'q' }],
    ['permission_request', {
      requestId: 'p1',
      title: 'Run tool?',
      options: [
        { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
        { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
      ]
    }],
    ['question_request', {
      requestId: 'q1',
      options: [{ optionId: 'yes', name: 'Yes' }]
    }]
  ], {
    onDecisionOption: (item, option) => chosen.push([item.requestId, option.optionId])
  });
  await act(async () => view.host.querySelector('[data-request-id="p1"] [data-option-id="allow"]').click());
  await act(async () => view.host.querySelector('[data-request-id="q1"] [data-option-id="yes"]').click());
  assert.deepEqual(chosen, [['p1', 'allow'], ['q1', 'yes']]);

  view.projection.selectDecisionOption('p1', 'allow', 'Allow');
  view.projection.disableDecision('q1', 'Too late.');
  await view.render();
  assert.equal(view.host.querySelector('[data-request-id="p1"]').dataset.decisionState, 'disabled');
  assert.equal(view.host.querySelector('[data-request-id="p1"] [data-option-id="allow"]').getAttribute('aria-pressed'), 'true');
  assert.match(view.host.querySelector('[data-request-id="q1"]').textContent, /Too late/);
  await view.dispose();
});

const ELICITATION = ['elicitation_request', {
  requestId: 'e1',
  message: 'Pick your options.',
  schema: {
    properties: {
      choice: { type: 'string', title: 'Choice', enum: ['a', 'b'] },
      count: { type: 'integer', title: 'Count', default: 3 },
      flags: { type: 'array', title: 'Flags', items: { enum: ['x', 'y'] }, default: ['x'] },
      ok: { type: 'boolean', title: 'OK', default: true },
      reason: { type: 'string', title: 'Reason' }
    },
    required: ['choice', 'reason']
  }
}];

test('elicitation blocks an invalid required-field submission', async () => {
  const actions = [];
  const view = await renderEvents([ELICITATION], {
    onElicitationAction: (item, payload, statusText) =>
      actions.push({ requestId: item.requestId, payload, statusText })
  });
  const continueButton = [...view.host.querySelectorAll('button')].find((button) => button.textContent === 'Continue');
  await act(async () => continueButton.click());
  assert.equal(actions.length, 0);
  assert.match(view.host.querySelector('.agent-elicitation-field-error').textContent, /Enter a response/);
  await view.dispose();
});

test('a valid elicitation emits the exact accepted payload', async () => {
  const actions = [];
  const valid = structuredClone(ELICITATION);
  valid[1].schema.properties.reason.default = 'Because it is safe';
  const view = await renderEvents([valid], {
    onElicitationAction: (item, payload, statusText) =>
      actions.push({ requestId: item.requestId, payload, statusText })
  });
  const continueButton = [...view.host.querySelectorAll('button')].find(
    (button) => button.textContent === 'Continue'
  );
  await act(async () => continueButton.click());
  assert.deepEqual(actions, [{
    requestId: 'e1',
    payload: JSON.stringify({
      action: 'accept',
      content: { choice: 'a', count: 3, flags: ['x'], ok: true, reason: 'Because it is safe' }
    }),
    statusText: 'Response sent.'
  }]);
  await view.dispose();
});

test('elicitation option buttons render title and description and submit the picked value', async () => {
  const actions = [];
  const view = await renderEvents([
    ['elicitation_request', {
      requestId: 'e3',
      message: 'Pick one.',
      schema: {
        properties: {
          mode: {
            type: 'string',
            title: 'Mode',
            oneOf: [
              { const: 'fast', title: 'Fast', description: 'Skips checks' },
              { const: 'safe', title: 'Safe', description: 'Runs all checks' }
            ]
          }
        },
        required: ['mode']
      }
    }]
  ], {
    onElicitationAction: (item, payload, statusText) => actions.push({ payload, statusText })
  });
  const options = [...view.host.querySelectorAll('.agent-elicitation-option')];
  assert.equal(options.length, 2);
  assert.match(options[1].querySelector('.agent-elicitation-option-title').textContent, /Safe/);
  assert.match(options[1].querySelector('.agent-elicitation-option-description').textContent, /Runs all checks/);
  await act(async () => options[1].click());
  const picked = [...view.host.querySelectorAll('.agent-elicitation-option')][1];
  assert.equal(picked.getAttribute('aria-checked'), 'true');
  const continueButton = [...view.host.querySelectorAll('button')].find(
    (button) => button.textContent === 'Continue'
  );
  await act(async () => continueButton.click());
  assert.deepEqual(actions, [{
    payload: JSON.stringify({ action: 'accept', content: { mode: 'safe' } }),
    statusText: 'Response sent.'
  }]);
  // Production TimelineController disables the decision after posting the
  // response; a local answer folds the card to its header, the header shows
  // the resolution status and a click re-expands the historical form.
  view.projection.disableDecision('e3', 'Response sent.');
  await view.render();
  const card = view.host.querySelector('.agent-decision-elicitation');
  assert.equal(card.dataset.state, 'closed');
  assert.match(card.querySelector('.agent-decision-header-status').textContent, /Response sent/);
  await act(async () => card.querySelector('.agent-decision-header').click());
  assert.equal(view.host.querySelector('.agent-decision-elicitation').dataset.state, 'open');
  await view.dispose();
});

test('unsafe elicitation URLs remain plain text', async () => {
  const view = await renderEvents([
    ['elicitation_request', { requestId: 'e2', mode: 'url', url: 'javascript:alert(1)' }]
  ]);
  const url = view.host.querySelector('.agent-elicitation-url');
  assert.match(url.textContent, /javascript:alert/);
  assert.equal(url.querySelector('a'), null);
  await view.dispose();
});

test('streaming updates preserve node identity', async () => {
  installAgentRuntime();
  const projection = new TimelineProjection();
  projection.apply('user_message', { text: 'q' }, NAME);
  projection.apply('assistant_delta', { text: 'a' }, NAME);
  const host = document.createElement('div');
  document.body.appendChild(host);
  const island = mountTimelineIsland(host, (error) => {
    throw error;
  });
  const render = async () => {
    await act(async () => {
      island.render({
        rows: projection.snapshot().rows,
        assistantName: NAME,
        callbacks: BASE_CALLBACKS
      });
    });
  };
  await render();
  const body = host.querySelector('.agent-message-assistant .agent-message-body');
  projection.apply('assistant_delta', { text: 'b' }, NAME);
  await render();
  assert.equal(host.querySelector('.agent-message-assistant .agent-message-body'), body);
  assert.equal(body.dataset.raw, 'ab');
  await act(async () => island.dispose());
});

test('user messages over sixteen lines can be expanded and collapsed accessibly', async () => {
  installAgentRuntime();
  const original = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight');
  Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get() {
      return this.classList?.contains('agent-message-content') ? 400 : 0;
    }
  });
  try {
    const projection = new TimelineProjection();
    projection.apply(
      'user_message',
      { text: Array.from({ length: 17 }, (_, i) => 'line ' + i).join('\n') },
      NAME
    );
    const host = document.createElement('div');
    document.body.appendChild(host);
    const island = mountTimelineIsland(host, (error) => {
      throw error;
    });
    await act(async () => {
      island.render({
        rows: projection.snapshot().rows,
        assistantName: NAME,
        callbacks: BASE_CALLBACKS
      });
    });
    const button = host.querySelector('.agent-message-collapse-toggle');
    const body = host.querySelector('.agent-message-body');
    assert.ok(button);
    assert.equal(button.getAttribute('aria-expanded'), 'false');
    assert.ok(body.classList.contains('agent-message-collapsed'));
    await act(async () => button.click());
    assert.equal(button.getAttribute('aria-expanded'), 'true');
    assert.ok(!body.classList.contains('agent-message-collapsed'));
    await act(async () => island.dispose());
  } finally {
    if (original) Object.defineProperty(HTMLElement.prototype, 'scrollHeight', original);
    else delete HTMLElement.prototype.scrollHeight;
  }
});
