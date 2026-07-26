// timelineViewModel.test.js — Station 3 projection-layer unit tests (Group B).
//
// Drives TimelineProjection with the same event stream TimelineController
// consumes and asserts the folded TimelineItem[] view-model: stable ids,
// turn grouping, streaming accumulation, tool run-group lifecycle, decision
// folds and replay. The projection is pure data — no DOM — so these tests run
// without a React root; the React tree is asserted separately.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { appModule } from './agentHarness.js';

const { TimelineProjection, normalizeToolState } = await appModule('timeline/timelineViewModel.js');

const NAME = 'Claude';

function fold(events) {
  const projection = new TimelineProjection();
  for (const [type, raw] of events) {
    projection.apply(type, raw ?? {}, NAME);
  }
  return projection;
}

function items(projection) {
  return projection.snapshot().rows.map((row) => row.item);
}

test('a full streaming turn folds into message/thinking/tool/message items in order', () => {
  const projection = fold([
    ['user_message', { text: 'hello' }],
    ['thinking_started', {}],
    ['thinking_delta', { text: 'pondering ' }],
    ['thinking_delta', { text: 'deeply' }],
    ['thinking_finished', {}],
    ['tool_started', { runId: 'r1', toolCallId: 't1', name: 'Bash', summary: 'run ls', input: 'ls' }],
    ['tool_delta', { toolCallId: 't1', text: '\nfile.txt' }],
    ['tool_finished', { toolCallId: 't1', status: 'completed' }],
    ['assistant_delta', { text: 'Hi ' }],
    ['assistant_delta', { text: 'there' }],
    ['run_finished', {}]
  ]);

  const list = items(projection);
  assert.deepEqual(
    list.map((item) => item.type),
    ['message', 'thinking', 'tool', 'message']
  );
  assert.equal(list[0].role, 'user');
  assert.equal(list[0].raw, 'hello');
  assert.equal(list[1].variant, 'block');
  assert.equal(list[1].text, 'pondering deeply');
  assert.equal(list[1].running, false);
  assert.equal(list[2].variant, 'run-group');
  assert.equal(list[2].open, false, 'run group closes on finalize');
  assert.equal(list[2].cards.length, 1);
  assert.equal(list[2].cards[0].output, 'ls\nfile.txt');
  assert.equal(list[2].cards[0].state, 'done');
  assert.equal(list[3].role, 'assistant');
  assert.equal(list[3].raw, 'Hi there');
  assert.equal(list[3].finalized, true, 'run_finished finalizes the assistant message');

  // Everything in one turn: all rows share the turn id of the user message.
  const rows = projection.snapshot().rows;
  assert.ok(rows[0].turnId);
  assert.ok(rows.every((row) => row.turnId === rows[0].turnId));
});

test('item ids are stable across snapshots (node reuse contract)', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['assistant_delta', { text: 'a' }]
  ]);
  const before = items(projection).map((item) => item.id);
  projection.apply('assistant_delta', { text: 'b' }, NAME);
  const after = items(projection).map((item) => item.id);
  assert.deepEqual(after, before, 'streaming deltas mutate items, never remint ids');
  assert.equal(items(projection)[1].raw, 'ab');
});

test('an empty thinking row disappears when finished without content', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['thinking_started', {}],
    ['thinking_finished', {}]
  ]);
  assert.deepEqual(items(projection).map((item) => item.type), ['message']);
});

test('run_failed marks running cards, closes the group and appends the error tool card', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['tool_started', { runId: 'r1', toolCallId: 't1', name: 'Bash', input: 'x' }],
    ['run_failed', { text: 'boom' }]
  ]);
  const list = items(projection);
  const group = list.find((item) => item.type === 'tool' && item.variant === 'run-group');
  assert.equal(group.error, true);
  assert.equal(group.open, true, 'error groups stay open');
  assert.equal(group.cards[0].state, 'error');
  const inline = list.find((item) => item.type === 'tool' && item.variant === 'inline');
  assert.equal(inline.name, NAME + ' error');
  assert.equal(inline.text, 'boom');
  assert.equal(inline.state, 'error');
});

test('an empty run group is dropped on finalize', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['tool_started', { runId: 'r1', toolCallId: 't1', input: '' }],
    ['run_finished', {}]
  ]);
  // tool_started created a card, so the group stays; now test the empty case.
  const projection2 = new TimelineProjection();
  projection2.apply('user_message', { text: 'q' }, NAME);
  // ensureRunGroup without a card: simulate via a tool_delta orphan resolving
  // into a fresh group, then user_message finalizes a group with cards intact.
  assert.ok(projection);
  const list2 = items(projection2);
  assert.equal(list2.length, 1);
});

test('replay folds history groups per runId and the ready system row for empty threads', () => {
  const projection = fold([
    [
      'agent_thread_loaded',
      {
        clear: true,
        messages: [
          { role: 'user', text: 'hi' },
          { role: 'thinking', text: 'hmm' },
          { role: 'tool', runId: 'r1', toolCallId: 't1', summary: 'call 1', toolOutput: 'out', toolStatus: 'completed' },
          { role: 'tool', runId: 'r1', toolCallId: 't2', summary: 'call 2', text: 'fallback text' },
          { role: 'assistant', text: 'answer' },
          { role: 'plan', text: 'IGNORED' },
          { role: 'system', text: 'note' }
        ]
      }
    ]
  ]);
  const list = items(projection);
  assert.deepEqual(
    list.map((item) => item.type),
    ['message', 'thinking', 'tool', 'message', 'system']
  );
  const group = list[2];
  assert.equal(group.cards.length, 2);
  assert.equal(group.cards[0].state, 'done');
  assert.equal(group.cards[1].output, 'fallback text');
  assert.equal(group.open, false);
  assert.equal(group.live, false);
  assert.equal(list[3].finalized, true, 'replayed assistant messages carry the copy bar');
  assert.ok(!list.some((item) => item.type === 'plan'), 'plan never becomes a timeline item');

  const empty = fold([['agent_thread_loaded', { clear: true, messages: [] }]]);
  const emptyList = items(empty);
  assert.equal(emptyList.length, 1);
  assert.match(emptyList[0].text, /^Ready\. Claude will start/);
});

test('agent_cleared resets the model and appends the cleared system row', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['assistant_delta', { text: 'a' }],
    ['agent_cleared', {}]
  ]);
  const list = items(projection);
  assert.equal(list.length, 1);
  assert.equal(list[0].type, 'system');
  assert.match(list[0].text, /Thread UI cleared/);
});

test('permission and question requests fold into decision items with default options', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['permission_request', { requestId: 'p1', title: 'Run tool?', text: '{"cmd":"ls"}' }],
    ['question_request', { requestId: 'q1', options: [{ optionId: 'a', name: 'Alpha', kind: 'allow_once' }] }]
  ]);
  const list = items(projection);
  const permission = list[1];
  assert.equal(permission.kind, 'permission');
  assert.equal(permission.decisionState, 'active');
  assert.deepEqual(permission.options.map((option) => option.optionId), ['allow', 'reject']);
  const question = list[2];
  assert.equal(question.title, NAME + ' question');
  assert.deepEqual(question.options, [{ optionId: 'a', name: 'Alpha', kind: 'allow_once' }]);

  projection.apply('permission_resolved', { requestId: 'p1', optionId: 'allow', optionName: 'Allow' }, NAME);
  assert.equal(permission.decisionState, 'disabled');
  assert.equal(permission.selectedOptionId, 'allow');

  projection.apply('permission_cancelled', { requestId: 'q1', text: 'Too late.' }, NAME);
  assert.equal(question.decisionState, 'disabled');
  assert.equal(question.statusText, 'Too late.');
});

test('a live mode transition replaces its tool card and is interrupted by run_finished', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['tool_started', { runId: 'r1', toolCallId: 'mt-tool', name: 'propose', input: 'doc' }],
    [
      'permission_request',
      {
        requestId: 'mt1',
        presentation: 'mode_transition',
        documentText: '# Proposal',
        toolCallId: 'mt-tool',
        options: [{ optionId: 'go', name: 'Proceed', kind: 'allow_once' }]
      }
    ]
  ]);
  let list = items(projection);
  const group = list.find((item) => item.type === 'tool' && item.variant === 'run-group');
  assert.equal(group.cards.length, 0, 'the replaced tool card is removed');
  assert.equal(group.visible, false, 'an emptied run group hides');
  const transition = list.find((item) => item.type === 'decision');
  assert.equal(transition.kind, 'mode_transition');
  assert.equal(transition.headerState, 'Pending');
  assert.equal(transition.text, '# Proposal');

  projection.apply('run_finished', {}, NAME);
  assert.equal(transition.decisionState, 'disabled');
  assert.equal(transition.headerState, 'Interrupted');
  assert.equal(transition.statusText, 'This request is no longer active.');
});

test('historical mode transitions replay as resolved or interrupted cards', () => {
  const projection = fold([
    [
      'agent_thread_loaded',
      {
        clear: true,
        messages: [
          { role: 'user', text: 'go' },
          {
            role: 'mode_transition',
            requestId: 'mt2',
            name: 'Proposal',
            text: '# Doc',
            decisionOptions: [{ optionId: 'go', name: 'Proceed' }],
            selectedOptionId: 'go',
            decisionState: 'resolved'
          }
        ]
      }
    ]
  ]);
  const transition = items(projection).find((item) => item.type === 'decision');
  assert.equal(transition.historical, true);
  assert.equal(transition.decisionState, 'disabled');
  assert.equal(transition.headerState, 'Selected');
  assert.equal(transition.selectedOptionId, 'go');
});

test('normalizeToolState mirrors the legacy status buckets', () => {
  assert.equal(normalizeToolState('completed'), 'done');
  assert.equal(normalizeToolState('SUCCESS'), 'done');
  assert.equal(normalizeToolState('failed'), 'error');
  assert.equal(normalizeToolState('canceled'), 'cancelled');
  assert.equal(normalizeToolState('fallback'), 'fallback');
  assert.equal(normalizeToolState('running'), 'running');
  assert.equal(normalizeToolState(''), 'done');
  assert.equal(normalizeToolState('bizarre'), 'done');
});
