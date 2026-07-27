// TimelineController.ts — React Timeline projection and side-effect owner.
//
// TimelineProjection is the single source of render state and one React root
// is the single writer for [data-role="thread"] children.
import { createInitialWorkspaceState } from '../contracts/workspace-state.js';
import { createIslandLoader } from '../core/islandHost.js';
import { TimelineProjection } from './timelineViewModel.js';
export class TimelineController {
    workspaceId;
    host;
    state;
    projection = new TimelineProjection();
    timelineIsland = null;
    autoScrollPinned = true;
    scrollListener = null;
    constructor(workspaceId, host) {
        this.workspaceId = workspaceId;
        this.host = host;
        this.state = createInitialWorkspaceState(workspaceId);
    }
    mount() {
        const thread = this.thread;
        if (!thread)
            return;
        this.scrollListener = () => {
            this.autoScrollPinned = this.isNearBottom();
        };
        thread.addEventListener('scroll', this.scrollListener);
        this.render();
    }
    update(event, state) {
        this.state = state;
        if (this.projection.apply(event.type, event.raw, this.assistantName)) {
            this.render();
        }
    }
    dispose() {
        const thread = this.thread;
        if (thread && this.scrollListener) {
            thread.removeEventListener('scroll', this.scrollListener);
        }
        this.scrollListener = null;
        this.timelineIsland?.dispose();
        this.timelineIsland = null;
    }
    appendSystemMessage(text) {
        this.projection.appendSystemMessage(text);
        this.render();
    }
    render() {
        const host = this.thread;
        if (!host)
            return;
        this.timelineIsland ??= createIslandLoader({
            name: 'timeline',
            load: async () => {
                const mod = await import('./timelineIsland.js');
                return (islandHost, reportFailure) => mod.mountTimelineIsland(islandHost, reportFailure);
            },
            host
        });
        this.timelineIsland.render({
            rows: this.projection.snapshot().rows,
            assistantName: this.assistantName,
            callbacks: this.callbacks(),
            onCommitted: () => this.scrollToBottom()
        });
    }
    callbacks() {
        return {
            copyText: (text) => this.copyText(text),
            onOpenTerminal: () => this.bridge()?.sendAgentCommand('terminal'),
            onDecisionOption: (item, option) => this.resolveDecision(item, option),
            onElicitationAction: (item, payload, statusText) => {
                if (!item.requestId)
                    return;
                this.bridge()?.sendAgentElicitationResponse(item.requestId, payload);
                this.projection.disableDecision(item.requestId, statusText);
                this.render();
            },
            createAttachmentTile: (attachment) => this.host.createMessageAttachmentTile(this.workspaceId, attachment)
        };
    }
    resolveDecision(item, option) {
        if (!item.requestId)
            return;
        if (item.kind === 'permission') {
            this.bridge()?.sendAgentPermissionResponse(item.requestId, option.optionId);
        }
        else if (item.kind === 'question') {
            this.bridge()?.sendAgentQuestionResponse(item.requestId, option.optionId);
        }
        else {
            return;
        }
        this.projection.selectDecisionOption(item.requestId, option.optionId, option.name);
        this.render();
    }
    async copyText(text) {
        const raw = typeof text === 'string' ? text : '';
        try {
            if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
                await navigator.clipboard.writeText(raw);
                return true;
            }
            return this.copyViaExecCommand(raw);
        }
        catch {
            try {
                return this.copyViaExecCommand(raw);
            }
            catch {
                return false;
            }
        }
    }
    copyViaExecCommand(text) {
        const previousActive = document.activeElement;
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.setAttribute('readonly', '');
        textarea.style.position = 'fixed';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);
        try {
            textarea.focus();
            textarea.select();
            return document.execCommand('copy');
        }
        finally {
            textarea.remove();
            previousActive?.focus();
        }
    }
    scrollToBottom() {
        const thread = this.thread;
        if (!thread || !this.autoScrollPinned)
            return;
        thread.scrollTop = thread.scrollHeight;
        this.autoScrollPinned = true;
    }
    isNearBottom() {
        const thread = this.thread;
        if (!thread)
            return true;
        return thread.scrollHeight - thread.scrollTop - thread.clientHeight <= 48;
    }
    get assistantName() {
        return this.state.identity.assistantName;
    }
    bridge() {
        return this.host.bridgeFor(this.workspaceId);
    }
    get thread() {
        return this.host
            .getPanel(this.workspaceId)
            ?.querySelector('[data-role="thread"]') ?? null;
    }
}
