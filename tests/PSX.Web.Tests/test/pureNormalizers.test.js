import { test } from 'vitest';
import assert from 'node:assert/strict';
import { appModule } from './agentHarness.js';

// The pure core/ modules own the Agent's data shaping (markdown, plan,
// commands, elicitation). The legacy AgentThreadManager they were extracted
// from is gone, so these assertions pin the extracted output directly rather
// than comparing against a live oracle. They must stay byte-faithful: the same
// input always produces the same HTML/records the shipped app renders.

const markdown = await appModule('core/markdown.js');
const plan = await appModule('core/plan.js');
const commands = await appModule('core/commands.js');
const elicitation = await appModule('core/elicitation.js');

// --- markdown --------------------------------------------------------------

test('markdown: renderMarkdown shapes headings, inline styles and code', () => {
  assert.equal(
    markdown.renderMarkdown('# Heading\n\nParagraph with **bold**, *italic* and `code`.'),
    '<h1>Heading</h1><p>Paragraph with <strong>bold</strong>, <em>italic</em> and <code>code</code>.</p>'
  );
  assert.equal(markdown.renderMarkdown('Line one\nline two'), '<p>Line one<br>line two</p>');
  assert.equal(markdown.renderMarkdown('- a\n- b\n- c'), '<ul><li>a</li><li>b</li><li>c</li></ul>');
  assert.equal(markdown.renderMarkdown('1. first\n2. second'), '<ol><li>first</li><li>second</li></ol>');
  assert.equal(markdown.renderMarkdown('> a quote'), '<blockquote>a quote</blockquote>');
  assert.equal(markdown.renderMarkdown('---'), '<hr>');
  assert.equal(
    markdown.renderMarkdown('mode_transition then switch_mode'),
    '<p>mode_transition then switch_mode</p>'
  );
  assert.equal(
    markdown.renderMarkdown('mode__transition then switch__mode'),
    '<p>mode__transition then switch__mode</p>'
  );
  assert.equal(markdown.renderMarkdown('_斜体_ and *斜体*'), '<p><em>斜体</em> and <em>斜体</em></p>');
});

test('markdown: renderMarkdown builds tables and fenced code blocks', () => {
  assert.equal(
    markdown.renderMarkdown('| A | B |\n| --- | --- |\n| 1 | 2 |'),
    '<div class="agent-table-scroll agent-native-scroll"><table><thead><tr><th>A</th><th>B</th></tr></thead>' +
      '<tbody><tr><td>1</td><td>2</td></tr></tbody></table></div>'
  );
  assert.equal(
    markdown.renderMarkdown('```js\nconst x = 1;\nconsole.log(x);\n```'),
    '<pre><code>const x = 1;\nconsole.log(x);</code></pre>'
  );
  assert.equal(
    markdown.renderMarkdown('Trailing text after code ```inline?``` end'),
    '<p>Trailing text after code</p><pre><code>inline?</code></pre><p>end</p>'
  );
});

test('markdown: renderMarkdown keeps unsafe links and script tags inert', () => {
  assert.equal(
    markdown.renderMarkdown('A [link](https://example.com) and a [bad](javascript:alert(1)) one.'),
    '<p>A <a href="https://example.com/" target="_blank" rel="noreferrer">link</a> and a bad) one.</p>'
  );
  assert.equal(
    markdown.renderMarkdown('Mix <b>safe</b> and <script>unsafe</script> tags & ampersand'),
    '<p>Mix <b>safe</b> and &lt;script&gt;unsafe&lt;/script&gt; tags &amp; ampersand</p>'
  );
});

test('markdown: escapeHtml and safeHref sanitize their inputs', () => {
  assert.equal(markdown.escapeHtml('<a>&"\''), '&lt;a&gt;&amp;&quot;&#039;');
  assert.equal(markdown.escapeHtml('plain'), 'plain');
  assert.equal(markdown.escapeHtml(''), '');

  assert.equal(markdown.safeHref('https://example.com/x'), 'https://example.com/x');
  assert.equal(markdown.safeHref('http://h'), 'http://h/');
  assert.equal(markdown.safeHref('mailto:a@b.com'), 'mailto:a@b.com');
  assert.equal(markdown.safeHref('javascript:evil'), '');
  assert.equal(markdown.safeHref('ftp://x'), '');
  assert.equal(markdown.safeHref('https://user:pass@h/'), '');
});

// --- plan ------------------------------------------------------------------

test('plan: normalizePlanEntries trims content and drops blank entries', () => {
  const entries = [
    { content: '  do a  ', status: 'completed', priority: 'high' },
    { content: 'do b', status: 'in_progress' },
    { content: '', status: 'pending' },
    { status: 'pending' },
    { content: 'do c', status: 'running', priority: '' }
  ];
  assert.deepEqual(plan.normalizePlanEntries(entries), [
    { content: 'do a', status: 'completed', priority: 'high' },
    { content: 'do b', status: 'in_progress', priority: '' },
    { content: 'do c', status: 'running', priority: '' }
  ]);
});

test('plan: status class/marker/label map every state family', () => {
  assert.equal(plan.planStatusClass('completed'), 'agent-plan-item-completed');
  assert.equal(plan.planStatusMarker('completed'), '\u2713');
  assert.equal(plan.planStatusLabel('completed'), 'Completed');

  for (const status of ['in_progress', 'in-progress', 'running', 'current']) {
    assert.equal(plan.planStatusClass(status), 'agent-plan-item-in-progress', `class ${status}`);
    assert.equal(plan.planStatusMarker(status), '\u25c9', `marker ${status}`);
    assert.equal(plan.planStatusLabel(status), 'Current', `label ${status}`);
  }

  for (const status of ['pending', 'weird', '']) {
    assert.equal(plan.planStatusClass(status), 'agent-plan-item-pending', `class ${status}`);
    assert.equal(plan.planStatusMarker(status), '\u25cb', `marker ${status}`);
    assert.equal(plan.planStatusLabel(status), 'Pending', `label ${status}`);
  }
});

test('plan: raw payload detection and reading only accept plan updates', () => {
  const rawPlan = JSON.stringify({ sessionUpdate: 'plan', entries: [{ content: 'x', status: 'pending' }] });
  assert.equal(plan.isRawPlanPayload(rawPlan), true);
  assert.deepEqual(plan.readRawPlanEntries(rawPlan), [{ content: 'x', status: 'pending', priority: '' }]);

  for (const text of [JSON.stringify({ sessionUpdate: 'other', entries: [] }), 'not json', '{bad', '']) {
    assert.equal(plan.isRawPlanPayload(text), false, `isRaw ${text}`);
    assert.deepEqual(plan.readRawPlanEntries(text), [], `readRaw ${text}`);
  }
});

// --- commands --------------------------------------------------------------

const psxCommands = [
  { name: '/help', command: 'help' },
  { name: '/cwd <path>', command: 'cwd' }
];
const agentCommands = [{ source: 'X Agent', name: '/review', label: 'Review code', agent: true }];

test('commands: parseLeadingSlashCommand splits name and arguments', () => {
  assert.deepEqual(commands.parseLeadingSlashCommand('/help'), { name: '/help', arguments: '' });
  assert.deepEqual(commands.parseLeadingSlashCommand('/cwd /some/path'), { name: '/cwd', arguments: '/some/path' });
  assert.equal(commands.parseLeadingSlashCommand('plain text'), null);
  assert.deepEqual(commands.parseLeadingSlashCommand('  /stop  arg '), { name: '/stop', arguments: 'arg' });
  assert.deepEqual(commands.parseLeadingSlashCommand('/'), { name: '/', arguments: '' });
});

test('commands: validateSubmissionCommand authorizes only known commands', () => {
  assert.deepEqual(
    commands.validateSubmissionCommand('/help', [], psxCommands, agentCommands, true),
    { allowed: true, text: '/help', psxCommand: 'help' }
  );
  assert.deepEqual(
    commands.validateSubmissionCommand('/review some args', [], psxCommands, agentCommands, true),
    { allowed: true, text: '/review some args', psxCommand: '' }
  );
  assert.deepEqual(
    commands.validateSubmissionCommand('/unknown', [], psxCommands, agentCommands, true),
    { allowed: false, command: '/unknown', reason: 'unsupported' }
  );
  assert.deepEqual(
    commands.validateSubmissionCommand('/help', ['att-1'], psxCommands, agentCommands, true),
    { allowed: false, command: '/help', reason: 'attachments_not_allowed' }
  );
  assert.deepEqual(
    commands.validateSubmissionCommand('plain message', [], psxCommands, agentCommands, true),
    { allowed: true, text: 'plain message' }
  );
  assert.deepEqual(
    commands.validateSubmissionCommand('/cwd /tmp', [], psxCommands, agentCommands, true),
    { allowed: true, text: '/cwd /tmp', psxCommand: 'cwd' }
  );
});

test('commands: unmatched command while loading reports commands_loading', () => {
  const result = commands.validateSubmissionCommand('/nope', [], psxCommands, [], false);
  assert.deepEqual(result, { allowed: false, command: '/nope', reason: 'commands_loading' });
});

// --- elicitation -----------------------------------------------------------

test('elicitation: readElicitationOptions flattens oneOf/anyOf/enum', () => {
  assert.deepEqual(
    elicitation.readElicitationOptions({ oneOf: [{ const: 'a', title: 'Alpha', description: 'first' }, { const: 'b' }] }),
    [{ value: 'a', title: 'Alpha', description: 'first' }, { value: 'b', title: 'b', description: '' }]
  );
  assert.deepEqual(
    elicitation.readElicitationOptions({ anyOf: [{ const: 1, title: 'One' }] }),
    [{ value: 1, title: 'One', description: '' }]
  );
  assert.deepEqual(
    elicitation.readElicitationOptions({ enum: ['x', 'y', 'z'] }),
    [{ value: 'x', title: 'x', description: '' }, { value: 'y', title: 'y', description: '' }, { value: 'z', title: 'z', description: '' }]
  );
  assert.deepEqual(elicitation.readElicitationOptions({ type: 'string' }), []);
});

test('elicitation: normalizeElicitationOption fills title and description fallbacks', () => {
  assert.deepEqual(elicitation.normalizeElicitationOption('v', '', 'd'), { value: 'v', title: 'v', description: 'd' });
  assert.deepEqual(elicitation.normalizeElicitationOption(null, null, null), { value: null, title: '', description: '' });
});

test('elicitation: defaultOptionValue prefers an exact match then optional first', () => {
  const options = [{ value: 'a' }, { value: 'b' }];
  assert.equal(elicitation.defaultOptionValue(options, 'b', false), 'b');
  assert.equal(elicitation.defaultOptionValue(options, undefined, true), 'a');
  assert.equal(elicitation.defaultOptionValue(options, undefined, false), undefined);
});

test('elicitation: supplemental fields are optional free-text prompts', () => {
  assert.equal(elicitation.isSupplementalElicitationField('comment', { type: 'string' }, false), true);
  assert.equal(elicitation.isSupplementalElicitationField('other', { type: 'string', title: 'Other' }, false), true);
  assert.equal(elicitation.isSupplementalElicitationField('name', { type: 'string', title: 'Name' }, false), false);
  assert.equal(elicitation.isSupplementalElicitationField('comment', { type: 'string' }, true), false);
  assert.equal(elicitation.isSupplementalElicitationField('other', { type: 'number' }, false), false);
});

test('elicitation: orderElicitationFields keeps schema declaration order', () => {
  const fields = elicitation.orderElicitationFields({
    properties: {
      q1: { type: 'string', title: 'Question 1', enum: ['a', 'b'] },
      q1_other: { type: 'string', title: 'Other' },
      q2: { type: 'string', title: 'Question 2', enum: ['c', 'd'] },
      q2_other: { type: 'string', title: 'Other' }
    },
    required: ['q1', 'q2']
  });
  assert.deepEqual(fields.map((field) => field.name), ['q1', 'q1_other', 'q2', 'q2_other']);
  assert.deepEqual(fields.map((field) => field.isSupplement), [false, true, false, true]);
});
