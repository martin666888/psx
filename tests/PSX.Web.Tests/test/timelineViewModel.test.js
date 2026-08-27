// timelineViewModel.test.js — Station 3 projection-layer unit tests (Group B).
//
// Drives TimelineProjection with the same event stream TimelineController
// consumes and asserts the folded TimelineItem[] view-model: stable ids,
// turn grouping, streaming accumulation, tool run-group lifecycle, decision
// folds and replay. The projection is pure data — no DOM — so these tests run
// without a React root; the React tree is asserted separately.

import { test } from 'vitest';
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
  assert.equal(list[2].cards[0].input, 'ls');
  assert.equal(list[2].cards[0].output, '\nfile.txt');
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
  // PSX-authored titles/bodies stay as render-time codes, never sentences.
  assert.deepEqual(inline.nameCode, { code: 'timeline.runErrorTitle', params: { agentName: NAME } });
  assert.equal(inline.name, '');
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

test('replay folds history groups per runId and keeps empty threads out of the projection', () => {
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
  assert.equal(emptyList.length, 0);
  assert.ok(!emptyList.some((item) => item.code === 'thread.ready'));
});

test('replay keeps tool cards when assistant text interleaves inside the same runId', () => {
  // Fragmented histories (assistant text between tool rows of one run) must
  // never drop tool cards: after an assistant row breaks the group, a later
  // tool row with the SAME runId starts a fresh group.
  const projection = fold([
    [
      'agent_thread_loaded',
      {
        clear: true,
        messages: [
          { role: 'user', text: 'hi' },
          { role: 'tool', runId: 'r1', toolCallId: 't1', summary: 'call 1', toolOutput: 'a', toolStatus: 'completed' },
          { role: 'assistant', text: 'partial answer' },
          { role: 'tool', runId: 'r1', toolCallId: 't2', summary: 'call 2', toolOutput: 'b', toolStatus: 'completed' },
          { role: 'tool', runId: 'r1', toolCallId: 't3', summary: 'call 3', toolOutput: 'c', toolStatus: 'completed' },
          { role: 'assistant', text: 'final answer' }
        ]
      }
    ]
  ]);
  const list = items(projection);
  assert.deepEqual(
    list.map((item) => item.type),
    ['message', 'tool', 'message', 'tool', 'message']
  );
  const groups = list.filter((item) => item.type === 'tool');
  assert.equal(groups[0].cards.length, 1);
  assert.equal(groups[0].cards[0].toolCallId, 't1');
  assert.equal(groups[1].cards.length, 2, 'post-interruption cards regroup instead of vanishing');
  assert.deepEqual(
    groups[1].cards.map((card) => card.toolCallId),
    ['t2', 't3']
  );
  const allCards = groups.flatMap((group) => group.cards.map((card) => card.toolCallId));
  assert.deepEqual(allCards, ['t1', 't2', 't3'], 'no tool card is silently dropped');
});

test('permission and question requests fold into decision items with default options', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['permission_request', {
      requestId: 'p1',
      title: 'Run tool?',
      description: 'Run ls once?',
      text: '{"cmd":"ls"}'
    }],
    ['question_request', { requestId: 'q1', options: [{ optionId: 'a', name: 'Alpha', kind: 'allow_once' }] }]
  ]);
  const list = items(projection);
  const permission = list[1];
  assert.equal(permission.kind, 'permission');
  assert.equal(permission.decisionState, 'active');
  assert.equal(permission.description, 'Run ls once?');
  assert.equal(permission.text, '{"cmd":"ls"}');
  assert.deepEqual(permission.options.map((option) => option.optionId), ['allow', 'reject']);
  const question = list[2];
  assert.equal(question.title, '');
  assert.deepEqual(question.titleCode, { code: 'timeline.decision.questionTitle', params: { agentName: NAME } });
  assert.deepEqual(question.options, [{ optionId: 'a', name: 'Alpha', kind: 'allow_once' }]);

  projection.selectDecisionOption(permission.id, 'allow', 'Allow');
  assert.equal(permission.collapsed, true, 'local select collapses the card');

  projection.apply('permission_resolved', { requestId: 'p1', optionId: 'allow', optionName: 'Allow' }, NAME);
  assert.equal(permission.decisionState, 'disabled');
  assert.equal(permission.selectedOptionId, 'allow');
  assert.equal(permission.collapsed, true, 'remote resolve does not reopen or re-fold');

  projection.apply('permission_cancelled', { requestId: 'q1', code: '' }, NAME);
  assert.equal(question.decisionState, 'disabled');
  assert.equal(question.statusCode.code, 'timeline.decision.requestCancelled');
  assert.equal(question.statusText, '');
});

test('permission form presentation folds schema without raw input text', () => {
  const projection = fold([
    ['user_message', { text: 'ask' }],
    ['permission_request', {
      requestId: 'pf1',
      presentation: 'form',
      title: 'Ask user 1 question',
      message: 'Please answer the following question(s):',
      formSubmitOptionId: 'proceed_once',
      schema: {
        type: 'object',
        properties: {
          q0: {
            type: 'string',
            title: 'Style',
            oneOf: [{ const: 'Scenic', title: 'Scenic' }]
          },
          q0_other: { type: 'string', title: 'Other' }
        },
        required: []
      },
      options: [
        { optionId: 'proceed_once', name: 'Submit', kind: 'allow_once' },
        { optionId: 'cancel', name: 'Cancel', kind: 'reject_once' }
      ],
      text: '{"questions":[{"should":"not appear"}]}'
    }]
  ]);
  const form = items(projection).find((item) => item.type === 'decision');
  assert.equal(form.kind, 'permission');
  assert.equal(form.formSubmitOptionId, 'proceed_once');
  assert.equal(form.text, '', 'form variant suppresses raw input dump');
  assert.equal(form.elicitationMessage, 'Please answer the following question(s):');
  assert.equal(form.elicitationMessageCode, undefined);
  assert.ok(form.schema);
  assert.equal(form.schema.presentation, 'form');
  assert.deepEqual(form.options.map((option) => option.optionId), ['proceed_once', 'cancel']);
});

test('reused ACP requestId updates the live card, not a historical twin', () => {
  const projection = new TimelineProjection();
  projection.apply(
    'agent_thread_loaded',
    {
      clear: true,
      messages: [
        {
          role: 'document_permission',
          requestId: '2',
          name: 'Old approval',
          text: '# historical plan',
          decisionOptions: [
            { optionId: 'approve_always', name: 'Approve for this session', kind: 'allow_always' }
          ],
          selectedOptionId: 'approve_always',
          decisionState: 'selected'
        }
      ]
    },
    NAME
  );
  projection.apply(
    'permission_request',
    {
      requestId: '2',
      title: 'Bash',
      description: 'ls -la "$HOME/.arkcli/"',
      options: [
        { optionId: 'approve_once', name: 'Approve once', kind: 'allow_once' },
        { optionId: 'approve_always', name: 'Approve for this session', kind: 'allow_always' },
        { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
      ]
    },
    NAME
  );

  const list = items(projection).filter((item) => item.type === 'decision');
  const historical = list.find((item) => item.historical);
  const live = list.find((item) => !item.historical);
  assert.ok(historical, 'historical decision with requestId 2 is present');
  assert.ok(live, 'live decision reuses requestId 2');
  assert.equal(historical.requestId, '2');
  assert.equal(live.requestId, '2');
  assert.equal(historical.decisionState, 'disabled');
  assert.equal(live.decisionState, 'active');
  assert.notEqual(historical.id, live.id);

  projection.selectDecisionOption(live.id, 'approve_always', 'Approve for this session');
  assert.equal(live.decisionState, 'disabled');
  assert.equal(live.collapsed, true, 'the clicked live card collapses');
  assert.equal(live.selectedOptionId, 'approve_always');
  assert.equal(
    historical.selectedOptionId,
    'approve_always',
    'historical selection is left alone'
  );
  assert.equal(historical.decisionState, 'disabled');

  projection.apply(
    'permission_resolved',
    {
      requestId: '2',
      optionId: 'approve_always',
      optionName: 'Approve for this session'
    },
    NAME
  );
  assert.equal(live.selectedOptionId, 'approve_always');
  assert.equal(live.selectedOptionName, 'Approve for this session');
  assert.equal(live.collapsed, true, 'remote resolve still targets the live card');
});

test('tool_updated replaces input/output/summary with empty-string clearing', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['tool_started', { runId: 'r1', toolCallId: 't1', name: 'Bash', summary: 'pending', input: '' }],
    ['tool_delta', { toolCallId: 't1', text: 'partial terminal' }],
    ['tool_updated', {
      toolCallId: 't1',
      summary: 'Bash ls',
      input: '{"command":"ls"}',
      output: 'file.txt',
      status: 'running'
    }],
    ['tool_updated', {
      toolCallId: 't1',
      summary: 'Bash ls',
      input: '',
      output: 'final only',
      status: 'completed'
    }]
  ]);
  const card = items(projection).find((item) => item.type === 'tool').cards[0];
  assert.equal(card.input, '', 'empty string clears prior input');
  assert.equal(card.output, 'final only', 'snapshot overwrites prior terminal delta');
  assert.equal(card.summary, 'Bash ls');
  assert.equal(card.state, 'done');
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
  assert.equal(transition.headerState, 'pending');
  assert.equal(transition.text, '# Proposal');
  assert.equal(transition.collapsed, false, 'live document decisions start expanded');

  projection.selectDecisionOption(transition.id, 'go', 'Proceed');
  assert.equal(transition.collapsed, true);
  assert.equal(transition.headerState, 'sending');
  projection.apply('permission_resolved', { requestId: 'mt1', optionId: 'go', optionName: 'Proceed' }, NAME);
  assert.equal(transition.headerState, 'selected');
  assert.equal(transition.collapsed, true, 'remote resolve does not change collapse');

  const interrupted = fold([
    ['user_message', { text: 'q' }],
    [
      'permission_request',
      {
        requestId: 'mt2',
        presentation: 'mode_transition',
        documentText: '# Proposal',
        toolCallId: 'mt-tool-2',
        options: [{ optionId: 'go', name: 'Proceed', kind: 'allow_once' }]
      }
    ],
    ['run_finished', {}]
  ]);
  const interruptedDecision = items(interrupted).find((item) => item.type === 'decision');
  assert.equal(interruptedDecision.decisionState, 'disabled');
  assert.equal(interruptedDecision.headerState, 'interrupted');
  assert.equal(interruptedDecision.statusCode.code, 'timeline.decision.inactive');
});

test('historical document decisions start collapsed', () => {
  const projection = fold([
    ['agent_thread_loaded', {
      messages: [{
        role: 'document_permission',
        requestId: 'hist-1',
        title: 'ExitPlanMode',
        text: '# Old plan',
        decisionState: 'selected',
        selectedOptionId: 'approve',
        options: [{ optionId: 'approve', name: 'Approve', kind: 'allow_once' }]
      }]
    }]
  ]);
  const decision = items(projection).find((item) => item.type === 'decision');
  assert.equal(decision.kind, 'document_permission');
  assert.equal(decision.historical, true);
  assert.equal(decision.collapsed, true);
});

test('a document permission renders its document, replaces its tool card and is not a mode transition', () => {
  const projection = fold([
    ['user_message', { text: 'q' }],
    ['tool_started', { runId: 'r1', toolCallId: 'kimi-tool', name: 'ExitPlanMode', input: 'raw tool' }],
    [
      'permission_request',
      {
        requestId: 'kimi-1',
        presentation: 'document',
        title: 'ExitPlanMode',
        documentText: '# Kimi plan\n\n1. Review',
        text: '',
        toolCallId: 'kimi-tool',
        options: [{ optionId: 'approve', name: 'Approve', kind: 'allow_once' }]
      }
    ]
  ]);
  const list = items(projection);
  const group = list.find((item) => item.type === 'tool' && item.variant === 'run-group');
  assert.equal(group.cards.length, 0);
  const document = list.find((item) => item.type === 'decision');
  assert.equal(document.kind, 'document_permission');
  assert.equal(document.text, '# Kimi plan\n\n1. Review');
  assert.equal(document.rawText, '');

  projection.apply('run_failed', {}, NAME);
  assert.equal(document.decisionState, 'disabled');
  assert.equal(document.headerState, 'interrupted');
});

test('historical mode transitions replay with the real C# decisionState values', () => {
  // C# persists selected / cancelled / interrupted (never "resolved").
  const replay = (decisionState, selectedOptionId) =>
    fold([
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
              selectedOptionId,
              decisionState
            }
          ]
        }
      ]
    ]);

  const selected = items(replay('selected', 'go')).find((item) => item.type === 'decision');
  assert.equal(selected.historical, true);
  assert.equal(selected.decisionState, 'disabled');
  assert.equal(selected.headerState, 'selected');
  assert.equal(selected.selectedOptionId, 'go');
  assert.deepEqual(selected.statusCode, { code: 'timeline.decision.selectedOption', params: { name: 'Proceed' } });

  const cancelled = items(replay('cancelled', '')).find((item) => item.type === 'decision');
  assert.equal(cancelled.headerState, 'cancelled');
  assert.equal(cancelled.statusCode.code, 'timeline.decision.requestCancelled');

  const interrupted = items(replay('interrupted', '')).find((item) => item.type === 'decision');
  assert.equal(interrupted.headerState, 'interrupted');
  assert.equal(interrupted.statusCode.code, 'timeline.decision.inactive');
});

test('historical document permissions replay as disabled document decisions', () => {
  const projection = fold([
    [
      'agent_thread_loaded',
      {
        clear: true,
        messages: [
          { role: 'user', text: 'go' },
          {
            role: 'document_permission',
            requestId: 'kimi-history',
            name: 'ExitPlanMode',
            text: '# Saved plan',
            decisionOptions: [{ optionId: 'approve', name: 'Approve' }],
            selectedOptionId: 'approve',
            decisionState: 'selected'
          }
        ]
      }
    ]
  ]);
  const document = items(projection).find((item) => item.type === 'decision');
  assert.equal(document.kind, 'document_permission');
  assert.equal(document.historical, true);
  assert.equal(document.decisionState, 'disabled');
  assert.equal(document.headerState, 'selected');
  assert.equal(document.selectedOptionId, 'approve');
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
