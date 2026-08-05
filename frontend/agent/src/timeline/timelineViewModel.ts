// timelineViewModel.ts — the Station 3 render-layer projection.
//
// Folds the same AgentWorkspaceEvent stream TimelineController consumes into a
// pure TimelineItem[] view-model with stable ids, so a single React tree can
// own the whole thread subtree (single-DOM-owner rule: messages, thinking,
// tool run-groups, system rows, recovery and decision cards all become data).
// The reducer's external behavior is untouched — this projection is
// controller-local render state, exactly like the DOM bookkeeping fields it
// replaces. TimelineItem deliberately has no 'plan' type: the Plan is a
// Workspace context card, never chat content.
//
// Every fold preserves the established event order and presentation state
// while remaining independent from React and the bridge decoder.

import type { RawHostMessage } from '../contracts/host-events.js';

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function asMessageArray(value: unknown): RawHostMessage[] {
  return Array.isArray(value) ? (value as RawHostMessage[]) : [];
}

// Faithful ports of _modeTransitionStateLabel / _modeTransitionStatusText for
// the non-active states C# actually persists: selected / cancelled /
// interrupted (DocumentDecisionSnapshotMerger folds pending/sending into
// interrupted on save).
function modeTransitionHeaderLabel(state: string): string {
  if (state === 'selected') return 'Selected';
  if (state === 'cancelled') return 'Cancelled';
  return 'Interrupted';
}

function modeTransitionStatusText(
  state: string,
  options: readonly DecisionOptionVM[],
  selectedOptionId: string
): string {
  if (state === 'selected') {
    const selected = options.find((option) => option.optionId === selectedOptionId);
    return selected ? 'Selected: ' + selected.name : 'Selection recorded.';
  }
  if (state === 'cancelled') return 'Request cancelled.';
  return 'This request is no longer active.';
}

// --- Item model ------------------------------------------------------------

export interface AttachmentVM {
  clientId: string;
  id: string;
  fileName: string;
  mimeType: string;
  size: number;
  status: string;
  url: string;
}

export interface MessageItem {
  type: 'message';
  id: string;
  role: 'user' | 'assistant';
  /** Raw markdown source (assistant deltas accumulate here). */
  raw: string;
  /** True once the copy action bar may render (assistant finalized). */
  finalized: boolean;
  attachments: AttachmentVM[];
}

export interface ThinkingItem {
  type: 'thinking';
  id: string;
  /** 'row' = spinner-only agent-thinking row; 'block' = details block. */
  variant: 'row' | 'block';
  text: string;
  running: boolean;
}

export interface ToolCardVM {
  toolCallId: string;
  summary: string;
  /** Explicit tool input (rawInput); never intermediate param snapshots. */
  input: string;
  /** Tool output / terminal stream; never mixed with pending JSON params. */
  output: string;
  /** When true, the Input section starts collapsed (long input). */
  inputCollapsed: boolean;
  /** normalized: running | done | error | cancelled | fallback */
  state: string;
  open: boolean;
  /** legacy deletes finished cards from the live map; deltas no longer target them. */
  forgotten?: boolean;
}

export interface ToolGroupItem {
  type: 'tool';
  id: string;
  variant: 'run-group';
  runId: string;
  open: boolean;
  error: boolean;
  /** display:none until the first card lands (legacy createRunGroup). */
  visible: boolean;
  live: boolean;
  cards: ToolCardVM[];
}

export interface InlineToolItem {
  type: 'tool';
  id: string;
  variant: 'inline';
  name: string;
  text: string;
  /** agent-tool-<state>: output | done | error | fallback */
  state: string;
}

export interface SystemItem {
  type: 'system';
  id: string;
  text: string;
}

export interface RecoveryItem {
  type: 'system';
  id: string;
  variant: 'recovery';
  message: string;
  detail: string;
}

export interface DecisionOptionVM {
  optionId: string;
  name: string;
  kind: string;
}

export interface DecisionItem {
  type: 'decision';
  id: string;
  kind: 'permission' | 'question' | 'elicitation' | 'mode_transition' | 'document_permission';
  requestId: string;
  /** Optional persisted snapshot key; live resolve stays requestId-based. */
  decisionSnapshotId?: string;
  title: string;
  /** Raw input JSON / message / document markdown depending on kind. */
  text: string;
  /** Ordinary permission one-line explanation; never document body. */
  description: string;
  /** Explicit technical tool input for document decisions; never a toolCall fallback. */
  rawText: string;
  options: DecisionOptionVM[];
  /** active | disabled */
  decisionState: 'active' | 'disabled';
  /** True only after a local option click (legacy closes the details then). */
  collapsed: boolean;
  selectedOptionId: string;
  selectedOptionName: string;
  /** Status line for cancelled/interrupted cards ('' hides it). */
  statusText: string;
  /** Document decisions: Pending / Selected / Cancelled / Interrupted header state. */
  headerState: string;
  /** Document decision: the replaced tool card id. */
  toolCallId: string;
  /** Document-decision replay cards render already-resolved. */
  historical: boolean;
  /** elicitation / permission-form raw schema payload (form rendering source). */
  schema: RawHostMessage | null;
  elicitationMessage: string;
  /**
   * Permission form variant: offered optionId to send with answers on Continue.
   * Empty for ordinary permission / elicitation cards.
   */
  formSubmitOptionId: string;
}

export type TimelineItem =
  | MessageItem
  | ThinkingItem
  | ToolGroupItem
  | InlineToolItem
  | SystemItem
  | RecoveryItem
  | DecisionItem;

/** A thread row: items with the same non-null turnId share an agent-turn section. */
export interface TimelineRow {
  turnId: string | null;
  item: TimelineItem;
}

export interface TimelineViewModel {
  rows: TimelineRow[];
}

// --- Projection --------------------------------------------------------------

/**
 * Controller-local projection: apply() folds one event, returning true when the
 * model changed (the controller then notifies the React root through its
 * subscription adapter). All bookkeeping mirrors the legacy fields 1:1.
 */
export class TimelineProjection {
  private rows: TimelineRow[] = [];
  private seq = 0;

  private currentTurnId: string | null = null;
  private currentAssistant: MessageItem | null = null;
  private currentInlineTool: InlineToolItem | null = null;
  private thinking: ThinkingItem | null = null;
  private currentGroup: ToolGroupItem | null = null;
  private currentToolCardId: string | null = null;
  private historyGroup: ToolGroupItem | null = null;

  private nextId(prefix: string): string {
    return prefix + '-' + ++this.seq;
  }

  snapshot(): TimelineViewModel {
    // Rows/items are mutated in place; hand the reader a shallow copy so React
    // sees a new array identity per change batch.
    return { rows: this.rows.slice() };
  }

  /** legacy _appendToCurrentHost: current turn when one is open, else thread. */
  private append(item: TimelineItem): void {
    this.rows.push({ turnId: this.currentTurnId, item });
  }

  /** Fold one workspace event; returns true when the view-model changed. */
  apply(type: string, raw: RawHostMessage, assistantName: string): boolean {
    switch (type) {
      case 'agent_thread_loaded':
        this.loadThread(raw, assistantName);
        return true;
      case 'command_result':
        this.appendSystem(asString(raw.text));
        return true;
      case 'user_message':
        this.finalizeRunGroup();
        this.startTurn();
        this.appendUserMessage(asString(raw.text), asMessageArray(raw.attachments));
        this.currentAssistant = null;
        return true;
      case 'thinking_started':
        this.showThinking();
        return true;
      case 'thinking_delta':
        this.appendThinkingDelta(asString(raw.text));
        return true;
      case 'thinking_finished':
        this.hideThinking();
        return true;
      case 'assistant_delta':
        this.appendAssistantDelta(asString(raw.text));
        return true;
      case 'assistant_message_done':
        if (this.currentAssistant) this.currentAssistant.finalized = true;
        this.currentAssistant = null;
        return true;
      case 'run_finished':
        if (this.currentAssistant) this.currentAssistant.finalized = true;
        this.currentAssistant = null;
        this.hideThinking();
        this.finalizeRunGroup();
        this.currentTurnId = null;
        this.interruptDocumentDecisions('This request is no longer active.');
        return true;
      case 'tool_started':
        this.ensureRunGroup(asString(raw.runId));
        this.createToolCard(
          asString(raw.toolCallId) || ('local-' + Date.now()),
          asString(raw.summary) || asString(raw.name) || 'Tool',
          asString(raw.input),
          '',
          'running'
        );
        return true;
      case 'tool_updated': {
        const card = this.resolveToolCard(raw);
        if (card) {
          // Full snapshot assignment — empty string clears prior values.
          card.input = typeof raw.input === 'string' ? raw.input : asString(raw.input);
          card.output = typeof raw.output === 'string' ? raw.output : asString(raw.output);
          card.summary = typeof raw.summary === 'string' ? raw.summary : asString(raw.summary);
          card.state = normalizeToolState(asString(raw.status) || card.state || 'running');
          card.inputCollapsed = shouldCollapseToolInput(card.input);
        }
        return true;
      }
      case 'tool_delta': {
        const card = this.resolveToolCard(raw);
        if (card) {
          card.output += asString(raw.text);
        } else {
          this.appendInlineToolDelta(asString(raw.name) || 'Tool output', asString(raw.text));
        }
        return true;
      }
      case 'tool_finished': {
        const card = this.resolveToolCard(raw);
        if (card) {
          if (!card.output.trim() && !card.input.trim()) card.output = 'Finished.';
          card.state = normalizeToolState(asString(raw.status) || 'done');
          card.open = false;
          this.forgetToolCard(card.toolCallId);
        } else {
          this.finishInlineTool();
        }
        this.currentToolCardId = null;
        return true;
      }
      case 'raw_terminal_fallback':
        this.appendInlineTool(
          'Raw terminal fallback',
          asString(raw.text) || 'Unsupported interaction requires terminal fallback.',
          'fallback'
        );
        return true;
      case 'run_failed':
        this.interruptDocumentDecisions('The request ended before a selection was completed.');
        this.hideThinking();
        if (this.currentGroup) {
          this.currentGroup.error = true;
          for (const card of this.currentGroup.cards) {
            if (card.state === 'running') card.state = 'error';
          }
        }
        this.finalizeRunGroup();
        this.appendInlineTool(assistantName + ' error', asString(raw.text) || 'Unknown error.', 'error');
        if (asString(raw.visionContextHint)) this.appendSystem(asString(raw.visionContextHint));
        return true;
      case 'resume_failed':
        this.appendRecovery(
          asString(raw.message) || asString(raw.text) || (assistantName + ' could not resume this session.'),
          asString(raw.detail)
        );
        return true;
      // --- decisions domain (same thread subtree, same React tree) ---------
      case 'permission_request':
        if (raw.presentation === 'mode_transition' && raw.documentText) {
          this.appendDocumentDecision(raw, false, 'mode_transition');
        } else if (raw.presentation === 'document' && raw.documentText) {
          this.appendDocumentDecision(raw, false, 'document_permission');
        } else if (raw.presentation === 'form' && raw.schema) {
          this.appendPermissionForm(raw, assistantName);
        } else {
          this.appendDecision(raw, 'permission', assistantName);
        }
        return true;
      case 'question_request':
        this.appendDecision(raw, 'question', assistantName);
        return true;
      case 'elicitation_request':
        this.appendElicitation(raw, assistantName);
        return true;
      case 'permission_resolved':
        this.resolveDecision(asString(raw.requestId), asString(raw.optionId), asString(raw.optionName));
        return true;
      case 'permission_cancelled':
      case 'elicitation_cancelled':
        this.cancelDecision(asString(raw.requestId), asString(raw.text) || 'Request cancelled.');
        return true;
      default:
        return false;
    }
  }

  /** External resolution hook: an option button in the React tree was clicked.
   *  Look up by stable timeline item id — never by ACP requestId, which can
   *  collide with replayed historical decisions after a process restart. */
  selectDecisionOption(itemId: string, optionId: string, optionName: string): void {
    const item = this.findDecisionById(itemId);
    if (!item || item.decisionState !== 'active') return;
    item.decisionState = 'disabled';
    item.collapsed = true;
    item.selectedOptionId = optionId;
    item.selectedOptionName = optionName;
    if (item.kind === 'mode_transition' || item.kind === 'document_permission') {
      item.headerState = 'Sending';
    }
  }

  /** External hook: a locally-resolved elicitation (legacy disableDecisionCard).
   *  Collapses the card like selectDecisionOption: only a local answer folds
   *  the form; external cancels leave the user's toggle alone. */
  disableDecision(itemId: string, statusText: string): void {
    const item = this.findDecisionById(itemId);
    if (!item || item.decisionState !== 'active') return;
    item.decisionState = 'disabled';
    item.collapsed = true;
    item.statusText = statusText;
  }

  /** Composer/notice seam: append a system row (legacy _appendSystem). */
  appendSystemMessage(text: string): void {
    this.appendSystem(text);
  }

  // --- turns / system ---------------------------------------------------------

  private startTurn(): void {
    this.currentTurnId = this.nextId('turn');
  }

  private appendSystem(text: string): void {
    this.append({ type: 'system', id: this.nextId('sys'), text });
  }

  private appendRecovery(message: string, detail: string): void {
    // Singleton card: reuse and move to the end of the thread (legacy re-appends).
    const index = this.rows.findIndex(
      (row) => row.item.type === 'system' && (row.item as RecoveryItem).variant === 'recovery'
    );
    let item: RecoveryItem;
    if (index >= 0) {
      item = this.rows.splice(index, 1)[0].item as RecoveryItem;
    } else {
      item = { type: 'system', id: this.nextId('rec'), variant: 'recovery', message: '', detail: '' };
    }
    item.message = message;
    item.detail = typeof detail === 'string' ? detail.trim() : '';
    // Recovery is appended to the thread root, never inside a turn.
    this.rows.push({ turnId: null, item });
  }

  // --- messages -----------------------------------------------------------------

  private appendUserMessage(text: string, attachments: RawHostMessage[]): void {
    if (!this.currentTurnId) this.startTurn();
    const item: MessageItem = {
      type: 'message',
      id: this.nextId('msg'),
      role: 'user',
      raw: text || '',
      finalized: false,
      attachments: attachments.map((attachment) => ({
        clientId: asString(attachment.clientId),
        id: asString(attachment.id),
        fileName: asString(attachment.fileName),
        mimeType: asString(attachment.mimeType),
        size: typeof attachment.size === 'number' ? attachment.size : 0,
        status: asString(attachment.status),
        url: asString(attachment.url)
      }))
    };
    this.append(item);
  }

  private appendAssistantMessage(text: string, finalized: boolean): MessageItem {
    if (!this.currentTurnId) this.startTurn();
    const item: MessageItem = {
      type: 'message',
      id: this.nextId('msg'),
      role: 'assistant',
      raw: text || '',
      finalized,
      attachments: []
    };
    this.append(item);
    return item;
  }

  private appendAssistantDelta(text: string): void {
    if (!this.currentAssistant) {
      this.currentAssistant = this.appendAssistantMessage('', false);
    }
    this.currentAssistant.raw += text;
  }

  // --- thinking -------------------------------------------------------------------

  private showThinking(): void {
    if (this.thinking) return;
    const item: ThinkingItem = {
      type: 'thinking',
      id: this.nextId('think'),
      variant: 'row',
      text: '',
      running: true
    };
    this.thinking = item;
    this.append(item);
  }

  private appendThinkingDelta(text: string): void {
    if (!text) return;
    if (!this.thinking || this.thinking.variant !== 'block') {
      // legacy _upgradeThinkingBlock replaces the row with a details block
      if (this.thinking) {
        this.thinking.variant = 'block';
      } else {
        const item: ThinkingItem = {
          type: 'thinking',
          id: this.nextId('think'),
          variant: 'block',
          text: '',
          running: true
        };
        this.thinking = item;
        this.append(item);
      }
    }
    this.thinking.text += text;
  }

  private hideThinking(): void {
    if (!this.thinking) return;
    if (!this.thinking.text) {
      // Row with no content is removed outright (legacy hideThinking).
      const index = this.rows.findIndex((row) => row.item === this.thinking);
      if (index >= 0) this.rows.splice(index, 1);
    } else {
      this.thinking.running = false;
    }
    this.thinking = null;
  }

  /** Replay: a finished thinking block from history. */
  private appendThinkingBlock(text: string): void {
    this.append({ type: 'thinking', id: this.nextId('think'), variant: 'block', text, running: false });
  }

  // --- tool run-groups ---------------------------------------------------------------

  private createRunGroup(runId: string): void {
    const item: ToolGroupItem = {
      type: 'tool',
      id: this.nextId('run'),
      variant: 'run-group',
      runId,
      open: true,
      error: false,
      visible: false,
      live: true,
      cards: []
    };
    this.append(item);
    this.currentGroup = item;
  }

  private ensureRunGroup(runId: string): void {
    if (this.currentAssistant && this.currentAssistant.raw.trim()) {
      this.currentAssistant = null;
    }
    if (!runId) {
      if (!this.currentGroup) this.createRunGroup('local-' + Date.now());
      return;
    }
    if (this.currentGroup && this.currentGroup.runId === runId) return;
    this.finalizeRunGroup();
    this.createRunGroup(runId);
  }

  private createToolCard(
    toolCallId: string,
    summary: string,
    input: string,
    output: string,
    state: string
  ): ToolCardVM | null {
    if (!this.currentGroup) return null;
    const existing = this.currentGroup.cards.find((card) => card.toolCallId === toolCallId);
    if (existing) {
      if (input) {
        existing.input = input;
        existing.inputCollapsed = shouldCollapseToolInput(input);
      }
      if (output) existing.output = output;
      if (summary) existing.summary = summary;
      return existing;
    }
    const card: ToolCardVM = {
      toolCallId,
      summary,
      input,
      output,
      inputCollapsed: shouldCollapseToolInput(input),
      state: normalizeToolState(state),
      open: true
    };
    this.currentGroup.cards.push(card);
    this.currentGroup.visible = true;
    this.currentToolCardId = toolCallId;
    return card;
  }

  private resolveToolCard(raw: RawHostMessage): ToolCardVM | null {
    const requestedId = asString(raw.toolCallId) || this.currentToolCardId || '';
    if (requestedId && this.currentGroup) {
      const tracked = this.currentGroup.cards.find(
        (card) => card.toolCallId === requestedId && !card.forgotten
      );
      if (tracked) return tracked;
    }
    this.ensureRunGroup(asString(raw.runId));
    const toolCallId = requestedId || ('orphan-' + Date.now());
    return this.createToolCard(
      toolCallId,
      asString(raw.summary) || asString(raw.name) || 'Tool output',
      asString(raw.input),
      asString(raw.output),
      'running'
    );
  }

  /** legacy deletes finished cards from the live map (rendered DOM remains). */
  private forgetToolCard(toolCallId: string): void {
    const card = this.currentGroup?.cards.find((entry) => entry.toolCallId === toolCallId);
    if (card) card.forgotten = true;
  }

  private finalizeRunGroup(): void {
    if (!this.currentGroup) return;
    if (this.currentGroup.cards.length === 0) {
      const index = this.rows.findIndex((row) => row.item === this.currentGroup);
      if (index >= 0) this.rows.splice(index, 1);
    } else if (!this.currentGroup.error) {
      this.currentGroup.open = false;
    }
    this.currentGroup.live = false;
    this.currentGroup = null;
    this.currentToolCardId = null;
  }

  // --- inline tool sections ------------------------------------------------------------

  private appendInlineTool(name: string, text: string, state: string): InlineToolItem {
    const item: InlineToolItem = {
      type: 'tool',
      id: this.nextId('tool'),
      variant: 'inline',
      name,
      text,
      state
    };
    this.append(item);
    return item;
  }

  private appendInlineToolDelta(name: string, text: string): void {
    if (!this.currentInlineTool) {
      this.currentInlineTool = this.appendInlineTool(name, '', 'output');
    }
    this.currentInlineTool.text += text;
  }

  private finishInlineTool(): void {
    if (!this.currentInlineTool) {
      this.appendInlineTool('Tool', 'Finished.', 'done');
      return;
    }
    this.currentInlineTool.text = this.currentInlineTool.text.trim()
      ? this.currentInlineTool.text + '\n\nFinished.'
      : 'Finished.';
    this.currentInlineTool = null;
  }

  // --- decisions --------------------------------------------------------------------------

  private findDecisionById(itemId: string): DecisionItem | null {
    if (!itemId) return null;
    for (const row of this.rows) {
      if (row.item.type === 'decision' && row.item.id === itemId) return row.item;
    }
    return null;
  }

  /**
   * Match a live ACP permission/question by requestId. Scan newest-first and
   * skip historical replay cards — JSON-RPC ids restart per process and often
   * collide with earlier decisions restored from the thread.
   */
  private findLiveDecisionByRequestId(requestId: string): DecisionItem | null {
    if (!requestId) return null;
    for (let index = this.rows.length - 1; index >= 0; index -= 1) {
      const item = this.rows[index].item;
      if (
        item.type === 'decision' &&
        !item.historical &&
        item.requestId === requestId
      ) {
        return item;
      }
    }
    return null;
  }

  private appendDecision(raw: RawHostMessage, kind: 'permission' | 'question', assistantName: string): void {
    const hasStructuredOptions = Array.isArray(raw.options);
    const options = hasStructuredOptions
      ? (raw.options as RawHostMessage[]).map((option) => ({
          optionId: asString(option.optionId),
          name: asString(option.name) || asString(option.optionId) || 'Select',
          kind: asString(option.kind)
        }))
      : [
          {
            optionId: kind === 'permission' ? 'allow' : 'yes',
            name: kind === 'permission' ? 'Allow' : 'Yes',
            kind: ''
          },
          {
            optionId: kind === 'permission' ? 'reject' : 'no',
            name: kind === 'permission' ? 'Reject' : 'No',
            kind: ''
          }
        ];
    this.append({
      type: 'decision',
      id: this.nextId('dec'),
      kind,
      requestId: asString(raw.requestId),
      decisionSnapshotId: asString(raw.decisionSnapshotId) || undefined,
      title:
        asString(raw.title) || (kind === 'permission' ? 'Permission request' : assistantName + ' question'),
      text: asString(raw.text),
      description: asString(raw.description),
      rawText: '',
      options,
      decisionState: 'active',
      collapsed: false,
      selectedOptionId: '',
      selectedOptionName: '',
      statusText: '',
      headerState: '',
      toolCallId: '',
      historical: false,
      schema: null,
      elicitationMessage: '',
      formSubmitOptionId: ''
    });
  }

  /**
   * Ask-user permission form: same pending permission channel, elicitation-shaped
   * schema so the shared form UI can collect answers without leaving the
   * agent_permission_response pairing.
   */
  private appendPermissionForm(raw: RawHostMessage, assistantName: string): void {
    const hasStructuredOptions = Array.isArray(raw.options);
    const options = hasStructuredOptions
      ? (raw.options as RawHostMessage[]).map((option) => ({
          optionId: asString(option.optionId),
          name: asString(option.name) || asString(option.optionId) || 'Select',
          kind: asString(option.kind)
        }))
      : [];
    this.append({
      type: 'decision',
      id: this.nextId('dec'),
      kind: 'permission',
      requestId: asString(raw.requestId),
      decisionSnapshotId: asString(raw.decisionSnapshotId) || undefined,
      title: asString(raw.title) || assistantName + ' Agent needs input',
      text: '',
      description: '',
      rawText: '',
      options,
      decisionState: 'active',
      collapsed: false,
      selectedOptionId: '',
      selectedOptionName: '',
      statusText: '',
      headerState: '',
      toolCallId: '',
      historical: false,
      schema: raw,
      elicitationMessage: asString(raw.message) || 'Provide the requested information to continue.',
      formSubmitOptionId: asString(raw.formSubmitOptionId)
    });
  }

  private appendElicitation(raw: RawHostMessage, assistantName: string): void {
    this.append({
      type: 'decision',
      id: this.nextId('dec'),
      kind: 'elicitation',
      requestId: asString(raw.requestId),
      decisionSnapshotId: asString(raw.decisionSnapshotId) || undefined,
      title: assistantName + ' Agent needs input',
      text: '',
      description: '',
      rawText: '',
      options: [],
      decisionState: 'active',
      collapsed: false,
      selectedOptionId: '',
      selectedOptionName: '',
      statusText: '',
      headerState: '',
      toolCallId: '',
      historical: false,
      schema: raw,
      elicitationMessage: asString(raw.message) || 'Provide the requested information to continue.',
      formSubmitOptionId: ''
    });
  }

  private appendDocumentDecision(
    raw: RawHostMessage,
    historical: boolean,
    kind: 'mode_transition' | 'document_permission'
  ): void {
    if (!historical && kind === 'mode_transition') {
      // legacy: a newer pending transition interrupts the older one.
      this.interruptModeTransition('A newer mode transition request replaced this one.');
    }
    if (!historical && asString(raw.toolCallId)) {
      this.removeToolCardForDocumentDecision(asString(raw.toolCallId));
    }
    if (!this.currentTurnId && historical) this.startTurn();
    // C# persists selected / cancelled / interrupted document decisions.
    const storedState = asString(raw.decisionState) || (historical ? 'interrupted' : 'pending');
    const options = asMessageArray(raw.options).map((option) => ({
      optionId: asString(option.optionId),
      name: asString(option.name) || asString(option.optionId) || 'Select',
      kind: asString(option.kind)
    }));
    const selectedOptionId = historical ? asString(raw.selectedOptionId) : '';
    this.append({
      type: 'decision',
      id: this.nextId('dec'),
      kind,
      requestId: asString(raw.requestId),
      decisionSnapshotId: asString(raw.decisionSnapshotId) || undefined,
      title: asString(raw.title) || asString(raw.name) || 'Review the proposed direction',
      text: asString(raw.documentText) || asString(raw.text),
      description: '',
      rawText: asString(raw.text),
      options,
      decisionState: historical ? 'disabled' : 'active',
      collapsed: historical,
      selectedOptionId,
      selectedOptionName: '',
      statusText: historical ? modeTransitionStatusText(storedState, options, selectedOptionId) : '',
      headerState: historical ? modeTransitionHeaderLabel(storedState) : 'Pending',
      toolCallId: asString(raw.toolCallId),
      historical,
      schema: null,
      elicitationMessage: '',
      formSubmitOptionId: ''
    });
  }

  /** Drop the tool card replaced by a document decision. */
  private removeToolCardForDocumentDecision(toolCallId: string): void {
    if (!toolCallId) return;
    for (const row of this.rows) {
      if (row.item.type === 'tool' && row.item.variant === 'run-group') {
        const index = row.item.cards.findIndex((card) => card.toolCallId === toolCallId);
        if (index >= 0) {
          row.item.cards.splice(index, 1);
          if (row.item.cards.length === 0) row.item.visible = false;
        }
      }
    }
  }

  private resolveDecision(requestId: string, optionId: string, optionName: string): void {
    const item = this.findLiveDecisionByRequestId(requestId);
    if (!item) return;
    item.decisionState = 'disabled';
    // Remote resolve updates selection state only — never forces collapse.
    if (item.kind === 'mode_transition' || item.kind === 'document_permission') {
      item.selectedOptionId = optionId;
      item.headerState = 'Selected';
      item.statusText = optionName ? 'Selected: ' + optionName : 'Selection recorded.';
      return;
    }
    if (optionId) item.selectedOptionId = optionId;
    if (optionName) item.selectedOptionName = optionName;
    item.statusText = '';
  }

  private cancelDecision(requestId: string, text: string): void {
    const item = this.findLiveDecisionByRequestId(requestId);
    if (!item) return;
    item.decisionState = 'disabled';
    if (item.kind === 'mode_transition' || item.kind === 'document_permission') {
      item.headerState = 'Cancelled';
      item.statusText = text;
      return;
    }
    item.statusText = text;
  }

  private interruptModeTransition(text: string): void {
    this.interruptDocumentDecisions(text, 'mode_transition');
  }

  private interruptDocumentDecisions(
    text: string,
    kind?: 'mode_transition' | 'document_permission'
  ): void {
    for (const row of this.rows) {
      const item = row.item;
      if (
        item.type === 'decision' &&
        (item.kind === 'mode_transition' || item.kind === 'document_permission') &&
        (!kind || item.kind === kind) &&
        item.decisionState === 'active'
      ) {
        item.decisionState = 'disabled';
        item.headerState = 'Interrupted';
        item.statusText = text;
      }
    }
  }

  // --- replay / clear -------------------------------------------------------------------------

  private reset(): void {
    this.rows = [];
    this.currentTurnId = null;
    this.currentAssistant = null;
    this.currentInlineTool = null;
    this.thinking = null;
    this.currentGroup = null;
    this.currentToolCardId = null;
    this.historyGroup = null;
  }

  private createHistoryGroup(runId: string): void {
    const item: ToolGroupItem = {
      type: 'tool',
      id: this.nextId('hist'),
      variant: 'run-group',
      runId,
      open: false,
      error: false,
      visible: true,
      live: false,
      cards: []
    };
    this.append(item);
    this.historyGroup = item;
  }

  private appendHistoryToolCard(msg: RawHostMessage): void {
    if (!this.historyGroup) return;
    const input = asString(msg.toolInput);
    const output = asString(msg.toolOutput) || (!input ? asString(msg.text) : '');
    this.historyGroup.cards.push({
      toolCallId: asString(msg.toolCallId),
      summary: asString(msg.summary) || asString(msg.name) || 'Tool',
      input,
      output,
      inputCollapsed: shouldCollapseToolInput(input),
      state: normalizeToolState(asString(msg.toolStatus) || 'done'),
      open: false
    });
  }

  private loadThread(event: RawHostMessage, assistantName: string): void {
    if (event.clear) this.reset();

    const messages = asMessageArray(event.messages);
    let lastRunId: string | null = null;

    for (const msg of messages) {
      const role = asString(msg.role);

      if (role === 'user') {
        this.historyGroup = null;
        this.startTurn();
        this.appendUserMessage(asString(msg.text), asMessageArray(msg.attachments));
        lastRunId = null;
        continue;
      }
      if (role === 'thinking') {
        this.historyGroup = null;
        if (!this.currentTurnId) this.startTurn();
        this.appendThinkingBlock(asString(msg.text));
        lastRunId = null;
        continue;
      }
      if (role === 'plan') {
        // Plan replay is context-card-owned; only the grouping bookkeeping runs.
        this.historyGroup = null;
        lastRunId = null;
        continue;
      }
      if (role === 'mode_transition' || role === 'document_permission') {
        this.historyGroup = null;
        if (!this.currentTurnId) this.startTurn();
        this.appendDocumentDecision(
          {
            requestId: asString(msg.requestId),
            decisionSnapshotId: asString(msg.decisionSnapshotId),
            toolCallId: asString(msg.toolCallId),
            title: asString(msg.name),
            documentText: asString(msg.text),
            options: Array.isArray(msg.decisionOptions) ? msg.decisionOptions : [],
            selectedOptionId: asString(msg.selectedOptionId),
            decisionState: asString(msg.decisionState) || 'interrupted'
          },
          true,
          role
        );
        lastRunId = null;
        continue;
      }
      if (role === 'tool' && msg.runId) {
        const runId = asString(msg.runId);
        if (runId !== lastRunId) {
          this.createHistoryGroup(runId);
          lastRunId = runId;
        }
        this.appendHistoryToolCard(msg);
        continue;
      }
      // Any non-tool row breaks the current history group. Also forget the
      // run id: a later tool row with the SAME runId must start a fresh
      // group instead of matching lastRunId while historyGroup is null —
      // that combination silently dropped tool cards.
      this.historyGroup = null;
      lastRunId = null;
      if (role === 'tool') {
        this.appendInlineTool(asString(msg.name) || 'Tool', asString(msg.text), 'done');
      } else if (role === 'system') {
        this.appendSystem(asString(msg.text));
      } else {
        this.appendAssistantMessage(asString(msg.text), true);
      }
    }
    this.historyGroup = null;
    this.currentTurnId = null;

    if (messages.length === 0) {
      this.appendSystem(
        'Ready. ' +
          assistantName +
          ' will start on the first message. Working directory and session state are shown above.'
      );
    }
  }
}

/** Faithful port of _setToolCardState's normalization. */
export function normalizeToolState(state: string): string {
  const raw = String(state || 'done').toLowerCase();
  return ['done', 'completed', 'complete', 'success', 'succeeded'].includes(raw)
    ? 'done'
    : ['error', 'failed', 'failure'].includes(raw)
    ? 'error'
    : ['cancelled', 'canceled'].includes(raw)
    ? 'cancelled'
    : raw === 'fallback'
    ? 'fallback'
    : raw === 'running'
    ? 'running'
    : 'done';
}

/** Input longer than 240 chars or spanning more than 3 lines starts collapsed. */
export function shouldCollapseToolInput(input: string): boolean {
  if (!input) return false;
  if (input.length > 240) return true;
  let lines = 1;
  for (let index = 0; index < input.length; index += 1) {
    if (input[index] === '\n') {
      lines += 1;
      if (lines > 3) return true;
    }
  }
  return false;
}

export const TOOL_STATE_LABELS: Readonly<Record<string, string>> = {
  running: 'Running',
  done: 'Done',
  error: 'Failed',
  cancelled: 'Cancelled',
  fallback: 'Needs terminal'
};
