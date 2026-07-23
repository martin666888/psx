// AgentHistoryStore.ts — the single process-global Agent History state owner.
//
// Holds the normalized thread list, the request status machine fields and the
// provider catalog. Pure state + notify: the AgentHistoryRequestBroker writes
// through the apply* methods, and the global HistoryDockController subscribes
// to render from the same snapshot. Payload normalization moved here from the
// per-workspace reducer when History went global.
import { createInitialAgentHistoryState } from '../contracts/agent-history.js';
const DEFAULT_ERROR_TEXT = 'Unable to load Agent thread history.';
function asString(value) {
    return typeof value === 'string' ? value : '';
}
function nonEmptyTrimmed(value) {
    if (typeof value !== 'string')
        return null;
    const trimmed = value.trim();
    return trimmed ? trimmed : null;
}
/** Normalizes the agent_threads payload into the rows the History list renders. */
export function normalizeHistoryThreads(value) {
    const list = Array.isArray(value) ? value : [];
    return list.map((raw) => {
        const t = (raw ?? {});
        return {
            threadId: asString(t.threadId),
            title: asString(t.title),
            cwd: asString(t.cwd),
            updatedAt: asString(t.updatedAt),
            sessionId: asString(t.sessionId),
            // Missing, non-string or blank provider stays 'unknown'; display
            // mapping is a later step, so no branded default is guessed here.
            providerKey: nonEmptyTrimmed(t.provider) ?? 'unknown'
        };
    });
}
/** Normalizes the agent_providers payload into the global catalog. */
export function normalizeProviderCatalog(value) {
    const list = Array.isArray(value) ? value : [];
    return list
        .map((raw) => (raw ?? {}))
        .filter((item) => nonEmptyTrimmed(item.key) !== null)
        .map((item) => ({
        key: nonEmptyTrimmed(item.key) ?? '',
        displayName: asString(item.displayName),
        assistantName: asString(item.assistantName),
        isDefault: item.isDefault === true
    }));
}
export class AgentHistoryStore {
    state = createInitialAgentHistoryState();
    listeners = new Set();
    getState() {
        return this.state;
    }
    subscribe(listener) {
        this.listeners.add(listener);
        return () => {
            this.listeners.delete(listener);
        };
    }
    /** A load landed: replace the list, clear error/dirty/in-flight. */
    applyThreads(threads) {
        this.set({
            ...this.state,
            threads,
            status: 'idle',
            errorText: '',
            dirty: false,
            inFlightWorkspaceId: '',
            loaded: true
        }, { kind: 'threads' });
    }
    /** A load failed with a host error: keep the cached list, show the error. */
    applyError(text) {
        this.set({
            ...this.state,
            status: 'error',
            errorText: text || DEFAULT_ERROR_TEXT,
            dirty: false,
            inFlightWorkspaceId: ''
        }, { kind: 'error' });
    }
    /** A request wave started on a channel: initial loads show the loading
     * state, refreshes keep the current list (lightweight 'refreshing'). */
    applyLoading(status, inFlightWorkspaceId) {
        this.set({ ...this.state, status, inFlightWorkspaceId }, { kind: 'status' });
    }
    /** No workspace can carry the request: keep the cached list, stay dirty so
     * the next workspace creation/activation retries automatically. */
    applyUnavailable() {
        this.set({ ...this.state, status: 'unavailable', dirty: true, inFlightWorkspaceId: '' }, { kind: 'status' });
    }
    /** The in-flight carrier closed or the retry budget ran out: end the
     * loading state but stay dirty for the next create/activate trigger. */
    applyIdleDirty() {
        this.set({ ...this.state, status: 'idle', dirty: true, inFlightWorkspaceId: '' }, { kind: 'status' });
    }
    applyProviders(providers) {
        this.set({ ...this.state, providers }, { kind: 'providers' });
    }
    /** A row action failed: keep the catalog and its request state intact. */
    applyThreadOpenError(threadId, text) {
        if (!threadId)
            return;
        this.set({
            ...this.state,
            threadOpenError: {
                threadId,
                text: text || 'PSX could not open the selected Agent thread. Try again.'
            }
        }, { kind: 'thread-open-error' });
    }
    clearThreadOpenError() {
        if (!this.state.threadOpenError)
            return;
        this.set({ ...this.state, threadOpenError: null }, { kind: 'thread-open-error' });
    }
    set(next, notice) {
        this.state = next;
        for (const listener of this.listeners)
            listener(next, notice);
    }
}
