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
// Every fold mirrors a legacy DOM write in TimelineController/DecisionController
// (the faithful ports of thread.js/messages.js/thinking.js/tools.js/
// permissions.js/modeTransition.js), so the React tree that renders this model
// can stay byte-identical to the legacy engine.
function asString(value) {
    return typeof value === 'string' ? value : '';
}
function asMessageArray(value) {
    return Array.isArray(value) ? value : [];
}
// Faithful ports of _modeTransitionStateLabel / _modeTransitionStatusText for
// the non-active states C# actually persists: selected / cancelled /
// interrupted (ModeTransitionSnapshotMerger folds pending/sending into
// interrupted on save).
function modeTransitionHeaderLabel(state) {
    if (state === 'selected')
        return 'Selected';
    if (state === 'cancelled')
        return 'Cancelled';
    return 'Interrupted';
}
function modeTransitionStatusText(state, options, selectedOptionId) {
    if (state === 'selected') {
        const selected = options.find((option) => option.optionId === selectedOptionId);
        return selected ? 'Selected: ' + selected.name : 'Selection recorded.';
    }
    if (state === 'cancelled')
        return 'Request cancelled.';
    return 'This request is no longer active.';
}
// --- Projection --------------------------------------------------------------
/**
 * Controller-local projection: apply() folds one event, returning true when the
 * model changed (the controller then notifies the React root through its
 * subscription adapter). All bookkeeping mirrors the legacy fields 1:1.
 */
export class TimelineProjection {
    rows = [];
    seq = 0;
    currentTurnId = null;
    currentAssistant = null;
    currentInlineTool = null;
    thinking = null;
    currentGroup = null;
    currentToolCardId = null;
    historyGroup = null;
    nextId(prefix) {
        return prefix + '-' + ++this.seq;
    }
    snapshot() {
        // Rows/items are mutated in place; hand the reader a shallow copy so React
        // sees a new array identity per change batch.
        return { rows: this.rows.slice() };
    }
    /** legacy _appendToCurrentHost: current turn when one is open, else thread. */
    append(item) {
        this.rows.push({ turnId: this.currentTurnId, item });
    }
    /** Fold one workspace event; returns true when the view-model changed. */
    apply(type, raw, assistantName) {
        switch (type) {
            case 'agent_thread_loaded':
                this.loadThread(raw, assistantName);
                return true;
            case 'agent_cleared':
                this.reset();
                this.appendSystem('Thread UI cleared. ' + assistantName + ' session context is unchanged.');
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
                if (this.currentAssistant)
                    this.currentAssistant.finalized = true;
                this.currentAssistant = null;
                return true;
            case 'run_finished':
                if (this.currentAssistant)
                    this.currentAssistant.finalized = true;
                this.currentAssistant = null;
                this.hideThinking();
                this.finalizeRunGroup();
                this.currentTurnId = null;
                this.interruptModeTransition('This request is no longer active.');
                return true;
            case 'tool_started':
                this.ensureRunGroup(asString(raw.runId));
                this.createToolCard(asString(raw.toolCallId) || ('local-' + Date.now()), asString(raw.summary) || asString(raw.name) || 'Tool', asString(raw.input), 'running');
                return true;
            case 'tool_delta': {
                const card = this.resolveToolCard(raw);
                if (card) {
                    card.output += asString(raw.text);
                }
                else {
                    this.appendInlineToolDelta(asString(raw.name) || 'Tool output', asString(raw.text));
                }
                return true;
            }
            case 'tool_finished': {
                const card = this.resolveToolCard(raw);
                if (card) {
                    if (!card.output.trim())
                        card.output = 'Finished.';
                    if (card.state === 'running' && this.currentGroup) {
                        // running count derives from card states at render time
                    }
                    card.state = normalizeToolState(asString(raw.status) || 'done');
                    card.open = false;
                    this.forgetToolCard(card.toolCallId);
                }
                else {
                    this.finishInlineTool();
                }
                this.currentToolCardId = null;
                return true;
            }
            case 'raw_terminal_fallback':
                this.appendInlineTool('Raw terminal fallback', asString(raw.text) || 'Unsupported interaction requires terminal fallback.', 'fallback');
                return true;
            case 'run_failed':
                this.interruptModeTransition('The request ended before a selection was completed.');
                this.hideThinking();
                if (this.currentGroup) {
                    this.currentGroup.error = true;
                    for (const card of this.currentGroup.cards) {
                        if (card.state === 'running')
                            card.state = 'error';
                    }
                }
                this.finalizeRunGroup();
                this.appendInlineTool(assistantName + ' error', asString(raw.text) || 'Unknown error.', 'error');
                if (asString(raw.visionContextHint))
                    this.appendSystem(asString(raw.visionContextHint));
                return true;
            case 'resume_failed':
                this.appendRecovery(asString(raw.message) || asString(raw.text) || (assistantName + ' could not resume this session.'), asString(raw.detail));
                return true;
            // --- decisions domain (same thread subtree, same React tree) ---------
            case 'permission_request':
                if (raw.presentation === 'mode_transition' && raw.documentText) {
                    this.appendModeTransition(raw, false);
                }
                else {
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
    /** External resolution hook: an option button in the React tree was clicked. */
    selectDecisionOption(requestId, optionId, optionName) {
        const item = this.findDecision(requestId);
        if (!item || item.decisionState !== 'active')
            return;
        item.decisionState = 'disabled';
        item.collapsed = true;
        item.selectedOptionId = optionId;
        item.selectedOptionName = optionName;
        if (item.kind === 'mode_transition')
            item.headerState = 'Sending';
    }
    /** External hook: a locally-resolved elicitation (legacy disableDecisionCard). */
    disableDecision(requestId, statusText) {
        const item = this.findDecision(requestId);
        if (!item || item.decisionState !== 'active')
            return;
        item.decisionState = 'disabled';
        item.statusText = statusText;
    }
    /** Composer/notice seam: append a system row (legacy _appendSystem). */
    appendSystemMessage(text) {
        this.appendSystem(text);
    }
    // --- turns / system ---------------------------------------------------------
    startTurn() {
        this.currentTurnId = this.nextId('turn');
    }
    appendSystem(text) {
        this.append({ type: 'system', id: this.nextId('sys'), text });
    }
    appendRecovery(message, detail) {
        // Singleton card: reuse and move to the end of the thread (legacy re-appends).
        const index = this.rows.findIndex((row) => row.item.type === 'system' && row.item.variant === 'recovery');
        let item;
        if (index >= 0) {
            item = this.rows.splice(index, 1)[0].item;
        }
        else {
            item = { type: 'system', id: this.nextId('rec'), variant: 'recovery', message: '', detail: '' };
        }
        item.message = message;
        item.detail = typeof detail === 'string' ? detail.trim() : '';
        // Recovery is appended to the thread root, never inside a turn.
        this.rows.push({ turnId: null, item });
    }
    // --- messages -----------------------------------------------------------------
    appendUserMessage(text, attachments) {
        if (!this.currentTurnId)
            this.startTurn();
        const item = {
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
    appendAssistantMessage(text, finalized) {
        if (!this.currentTurnId)
            this.startTurn();
        const item = {
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
    appendAssistantDelta(text) {
        if (!this.currentAssistant) {
            this.currentAssistant = this.appendAssistantMessage('', false);
        }
        this.currentAssistant.raw += text;
    }
    // --- thinking -------------------------------------------------------------------
    showThinking() {
        if (this.thinking)
            return;
        const item = {
            type: 'thinking',
            id: this.nextId('think'),
            variant: 'row',
            text: '',
            running: true
        };
        this.thinking = item;
        this.append(item);
    }
    appendThinkingDelta(text) {
        if (!text)
            return;
        if (!this.thinking || this.thinking.variant !== 'block') {
            // legacy _upgradeThinkingBlock replaces the row with a details block
            if (this.thinking) {
                this.thinking.variant = 'block';
            }
            else {
                const item = {
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
    hideThinking() {
        if (!this.thinking)
            return;
        if (!this.thinking.text) {
            // Row with no content is removed outright (legacy hideThinking).
            const index = this.rows.findIndex((row) => row.item === this.thinking);
            if (index >= 0)
                this.rows.splice(index, 1);
        }
        else {
            this.thinking.running = false;
        }
        this.thinking = null;
    }
    /** Replay: a finished thinking block from history. */
    appendThinkingBlock(text) {
        this.append({ type: 'thinking', id: this.nextId('think'), variant: 'block', text, running: false });
    }
    // --- tool run-groups ---------------------------------------------------------------
    createRunGroup(runId) {
        const item = {
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
    ensureRunGroup(runId) {
        if (this.currentAssistant && this.currentAssistant.raw.trim()) {
            this.currentAssistant = null;
        }
        if (!runId) {
            if (!this.currentGroup)
                this.createRunGroup('local-' + Date.now());
            return;
        }
        if (this.currentGroup && this.currentGroup.runId === runId)
            return;
        this.finalizeRunGroup();
        this.createRunGroup(runId);
    }
    createToolCard(toolCallId, summary, input, state) {
        if (!this.currentGroup)
            return null;
        const existing = this.currentGroup.cards.find((card) => card.toolCallId === toolCallId);
        if (existing) {
            if (input && !existing.output)
                existing.output = input;
            return existing;
        }
        const card = {
            toolCallId,
            summary,
            output: input,
            state: normalizeToolState(state),
            open: true
        };
        this.currentGroup.cards.push(card);
        this.currentGroup.visible = true;
        this.currentToolCardId = toolCallId;
        return card;
    }
    resolveToolCard(raw) {
        const requestedId = asString(raw.toolCallId) || this.currentToolCardId || '';
        if (requestedId && this.currentGroup) {
            const tracked = this.currentGroup.cards.find((card) => card.toolCallId === requestedId && !card.forgotten);
            if (tracked)
                return tracked;
        }
        this.ensureRunGroup(asString(raw.runId));
        const toolCallId = requestedId || ('orphan-' + Date.now());
        return this.createToolCard(toolCallId, asString(raw.summary) || asString(raw.name) || 'Tool output', asString(raw.input), 'running');
    }
    /** legacy deletes finished cards from the live map (rendered DOM remains). */
    forgetToolCard(toolCallId) {
        const card = this.currentGroup?.cards.find((entry) => entry.toolCallId === toolCallId);
        if (card)
            card.forgotten = true;
    }
    finalizeRunGroup() {
        if (!this.currentGroup)
            return;
        if (this.currentGroup.cards.length === 0) {
            const index = this.rows.findIndex((row) => row.item === this.currentGroup);
            if (index >= 0)
                this.rows.splice(index, 1);
        }
        else if (!this.currentGroup.error) {
            this.currentGroup.open = false;
        }
        this.currentGroup.live = false;
        this.currentGroup = null;
        this.currentToolCardId = null;
    }
    // --- inline tool sections ------------------------------------------------------------
    appendInlineTool(name, text, state) {
        const item = {
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
    appendInlineToolDelta(name, text) {
        if (!this.currentInlineTool) {
            this.currentInlineTool = this.appendInlineTool(name, '', 'output');
        }
        this.currentInlineTool.text += text;
    }
    finishInlineTool() {
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
    findDecision(requestId) {
        if (!requestId)
            return null;
        for (const row of this.rows) {
            if (row.item.type === 'decision' && row.item.requestId === requestId)
                return row.item;
        }
        return null;
    }
    appendDecision(raw, kind, assistantName) {
        const hasStructuredOptions = Array.isArray(raw.options);
        const options = hasStructuredOptions
            ? raw.options.map((option) => ({
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
            title: asString(raw.title) || (kind === 'permission' ? 'Permission request' : assistantName + ' question'),
            text: asString(raw.text) || '{}',
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
            elicitationMessage: ''
        });
    }
    appendElicitation(raw, assistantName) {
        this.append({
            type: 'decision',
            id: this.nextId('dec'),
            kind: 'elicitation',
            requestId: asString(raw.requestId),
            title: assistantName + ' Agent needs input',
            text: '',
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
            elicitationMessage: asString(raw.message) || 'Provide the requested information to continue.'
        });
    }
    appendModeTransition(raw, historical) {
        if (!historical) {
            // legacy: a newer pending transition interrupts the older one.
            this.interruptModeTransition('A newer mode transition request replaced this one.');
        }
        if (!historical && asString(raw.toolCallId)) {
            this.removeToolCardForModeTransition(asString(raw.toolCallId));
        }
        if (!this.currentTurnId && historical)
            this.startTurn();
        // C# persists decisionState as selected / cancelled / interrupted
        // (ModeTransitionSnapshotMerger folds pending/sending into interrupted).
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
            kind: 'mode_transition',
            requestId: asString(raw.requestId),
            title: asString(raw.title) || asString(raw.name) || 'Review the proposed direction',
            text: asString(raw.documentText) || asString(raw.text),
            options,
            decisionState: historical ? 'disabled' : 'active',
            collapsed: false,
            selectedOptionId,
            selectedOptionName: '',
            statusText: historical ? modeTransitionStatusText(storedState, options, selectedOptionId) : '',
            headerState: historical ? modeTransitionHeaderLabel(storedState) : 'Pending',
            toolCallId: asString(raw.toolCallId),
            historical,
            schema: null,
            elicitationMessage: ''
        });
    }
    /** legacy removeToolCardForModeTransition: drop the replaced tool card. */
    removeToolCardForModeTransition(toolCallId) {
        if (!toolCallId)
            return;
        for (const row of this.rows) {
            if (row.item.type === 'tool' && row.item.variant === 'run-group') {
                const index = row.item.cards.findIndex((card) => card.toolCallId === toolCallId);
                if (index >= 0) {
                    row.item.cards.splice(index, 1);
                    if (row.item.cards.length === 0)
                        row.item.visible = false;
                }
            }
        }
    }
    resolveDecision(requestId, optionId, optionName) {
        const item = this.findDecision(requestId);
        if (!item)
            return;
        item.decisionState = 'disabled';
        if (item.kind === 'mode_transition') {
            item.selectedOptionId = optionId;
            item.headerState = 'Selected';
            item.statusText = optionName ? 'Selected: ' + optionName : 'Selection recorded.';
            return;
        }
        if (optionId)
            item.selectedOptionId = optionId;
        if (optionName)
            item.selectedOptionName = optionName;
        item.statusText = '';
    }
    cancelDecision(requestId, text) {
        const item = this.findDecision(requestId);
        if (!item)
            return;
        item.decisionState = 'disabled';
        if (item.kind === 'mode_transition') {
            item.headerState = 'Cancelled';
            item.statusText = text;
            return;
        }
        item.statusText = text;
    }
    interruptModeTransition(text) {
        for (const row of this.rows) {
            const item = row.item;
            if (item.type === 'decision' &&
                item.kind === 'mode_transition' &&
                item.decisionState === 'active') {
                item.decisionState = 'disabled';
                item.headerState = 'Interrupted';
                item.statusText = text;
            }
        }
    }
    // --- replay / clear -------------------------------------------------------------------------
    reset() {
        this.rows = [];
        this.currentTurnId = null;
        this.currentAssistant = null;
        this.currentInlineTool = null;
        this.thinking = null;
        this.currentGroup = null;
        this.currentToolCardId = null;
        this.historyGroup = null;
    }
    createHistoryGroup(runId) {
        const item = {
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
    appendHistoryToolCard(msg) {
        if (!this.historyGroup)
            return;
        this.historyGroup.cards.push({
            toolCallId: asString(msg.toolCallId),
            summary: asString(msg.summary) || asString(msg.name) || 'Tool',
            output: asString(msg.toolOutput) || asString(msg.text),
            state: normalizeToolState(asString(msg.toolStatus) || 'done'),
            open: false
        });
    }
    loadThread(event, assistantName) {
        if (event.clear)
            this.reset();
        const messages = asMessageArray(event.messages);
        let lastRunId = null;
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
                if (!this.currentTurnId)
                    this.startTurn();
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
            if (role === 'mode_transition') {
                this.historyGroup = null;
                if (!this.currentTurnId)
                    this.startTurn();
                this.appendModeTransition({
                    requestId: asString(msg.requestId),
                    toolCallId: asString(msg.toolCallId),
                    title: asString(msg.name),
                    documentText: asString(msg.text),
                    options: Array.isArray(msg.decisionOptions) ? msg.decisionOptions : [],
                    selectedOptionId: asString(msg.selectedOptionId),
                    decisionState: asString(msg.decisionState) || 'interrupted'
                }, true);
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
            this.historyGroup = null;
            if (role === 'tool') {
                this.appendInlineTool(asString(msg.name) || 'Tool', asString(msg.text), 'done');
            }
            else if (role === 'system') {
                this.appendSystem(asString(msg.text));
            }
            else {
                this.appendAssistantMessage(asString(msg.text), true);
            }
        }
        this.historyGroup = null;
        this.currentTurnId = null;
        if (messages.length === 0) {
            this.appendSystem('Ready. ' +
                assistantName +
                ' will start on the first message. Working directory and session state are shown above.');
        }
    }
}
/** Faithful port of _setToolCardState's normalization. */
export function normalizeToolState(state) {
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
export const TOOL_STATE_LABELS = {
    running: 'Running',
    done: 'Done',
    error: 'Failed',
    cancelled: 'Cancelled',
    fallback: 'Needs terminal'
};
