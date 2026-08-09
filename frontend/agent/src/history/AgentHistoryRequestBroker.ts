// AgentHistoryRequestBroker.ts — the single owner of `history` bridge loads.
//
// History data is global. This broker prefers a live Agent workspace channel
// for compatibility and falls back to the process-wide bridge when none
// exists. It owns the request status
// machine (idle / initial-loading / refreshing / unavailable / error) and
// guards agent_threads / agent_history_error with a matching requestId (late or
// superseded replies are dropped) plus an in-flight workspace match and a
// monotonic epoch for its own timer callbacks. Every `history` command is
// broker-initiated and carries a requestId; a response with no matching
// in-flight request is dropped.
//
// Rules (approved design):
//   channel priority: originating workspace (alive) → active Agent workspace
//     → most recently activated live workspace → any live workspace. Busy and
//     transcript-only workspaces are valid channels; closing/closed/unknown
//     are not.
//   carrier closed mid-request: end the load, mark dirty, retry once on
//     another workspace or the global channel.
//   one 10s timeout per attempt, one retry per wave, then 'error'.
//   invalidation coalescing: a broadcast wave (one message per workspace)
//     collapses into a single refresh; an invalidation arriving mid-flight
//     queues at most one follow-up after the current response lands.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { RawHostMessage } from '../contracts/host-events.js';
import { WorkspaceChannelSelector } from '../workspace/WorkspaceChannelSelector.js';
import { AgentHistoryStore, normalizeHistoryThreads } from './AgentHistoryStore.js';

/** The registry-facing view of the live workspace set the broker needs. */
export interface AgentHistoryBrokerHost {
  isAlive(workspaceId: string): boolean;
  /** The active Agent workspace, or '' when a terminal/nothing is active. */
  activeAgentWorkspace(): string;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
  sendGlobalCommand(command: string, value?: string, requestId?: string): void;
}

export interface AgentHistoryRequestBrokerOptions {
  timeoutMs?: number;
}

const DEFAULT_TIMEOUT_MS = 10000;
const GLOBAL_CHANNEL = '@global';
const TIMEOUT_ERROR_TEXT = 'Loading Agent thread history timed out.';
const DEFAULT_ERROR_TEXT = 'Unable to load Agent thread history.';

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

export class AgentHistoryRequestBroker {
  private readonly host: AgentHistoryBrokerHost;
  private readonly store: AgentHistoryStore;
  private readonly timeoutMs: number;

  private readonly channels: WorkspaceChannelSelector;

  private inFlightWorkspaceId = '';
  private inFlightRequestId = '';
  private readonly requestInstanceId = crypto.randomUUID();
  private requestCounter = 0;
  private inFlightEpoch = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private epoch = 0;
  // One channel retry per wave (timeout or carrier loss).
  private retried = false;
  // Invalidation coalescing: at most one follow-up wave after a landing.
  private refreshQueued = false;
  // Same-tick debounce so a per-workspace invalidation broadcast is one wave.
  private invalidationTimer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;

  constructor(host: AgentHistoryBrokerHost, store: AgentHistoryStore, options?: AgentHistoryRequestBrokerOptions) {
    this.host = host;
    this.store = store;
    this.timeoutMs = options?.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    this.channels = new WorkspaceChannelSelector(host);
  }

  // --- Workspace registry hooks ---------------------------------------------

  registerWorkspace(workspaceId: string): void {
    if (!this.channels.register(workspaceId)) return;
    this.retryDirtyIfPossible();
  }

  activateWorkspace(workspaceId: string): void {
    if (!this.channels.activate(workspaceId)) return;
    this.retryDirtyIfPossible();
  }

  unregisterWorkspace(workspaceId: string): void {
    const id = String(workspaceId || '');
    if (!this.channels.unregister(id)) return;

    if (this.inFlightWorkspaceId !== id) return;
    // The carrier closed mid-request: end the load, mark dirty, and retry
    // once on another live channel; with none left the load is unavailable.
    this.clearInFlight();
    if (!this.retried) {
      const channel = this.pickChannel('');
      if (channel) {
        this.retried = true;
        this.sendRequest(channel);
        return;
      }
    }
    if (this.channels.size === 0) this.store.applyUnavailable();
    else this.store.applyIdleDirty();
  }

  // --- Request entry points ---------------------------------------------------

  /** User-initiated load (dock open, Refresh button, /history command). */
  requestRefresh(originWorkspaceId: string): void {
    if (this.disposed) return;
    if (this.inFlightWorkspaceId) {
      this.refreshQueued = true;
      return;
    }
    this.startWave(this.pickChannel(originWorkspaceId));
  }

  /** agent_history_invalidated: broadcast-coalesced background refresh.
   * Senders that are no longer alive (late broadcasts from closing or closed
   * workspaces) are ignored and never join the coalesced wave. */
  handleInvalidated(senderWorkspaceId: string): void {
    if (this.disposed) return;
    if (senderWorkspaceId
      && (!this.channels.has(senderWorkspaceId) || !this.host.isAlive(senderWorkspaceId))) return;
    if (this.invalidationTimer !== null) return;
    this.invalidationTimer = setTimeout(() => {
      this.invalidationTimer = null;
      if (this.disposed) return;
      if (this.inFlightWorkspaceId) {
        // One follow-up at most, after the current response lands.
        this.refreshQueued = true;
        return;
      }
      this.startWave(this.pickChannel(''));
    }, 0);
  }

  /** agent_threads response: accepted only when requestId and workspaceId match
   * the in-flight attempt. Every `history` command is broker-initiated, so a
   * response with no in-flight request (or from a stale/superseded attempt) is
   * dropped. */
  handleThreads(workspaceId: string, raw: RawHostMessage): void {
    if (this.disposed) return;
    const channel = workspaceId || GLOBAL_CHANNEL;
    if (channel !== GLOBAL_CHANNEL && !this.channels.has(channel)) return;
    if (!this.inFlightRequestId || asString(raw.requestId) !== this.inFlightRequestId) return;
    if (!this.inFlightWorkspaceId || this.inFlightWorkspaceId !== channel) return;
    this.clearInFlight();
    this.store.applyThreads(normalizeHistoryThreads(raw.threads));
    this.startQueuedWave();
  }

  /** agent_history_error response: same acceptance rules as agent_threads. */
  handleHistoryError(workspaceId: string, raw: RawHostMessage): void {
    if (this.disposed) return;
    const channel = workspaceId || GLOBAL_CHANNEL;
    if (channel !== GLOBAL_CHANNEL && !this.channels.has(channel)) return;
    if (!this.inFlightRequestId || asString(raw.requestId) !== this.inFlightRequestId) return;
    if (!this.inFlightWorkspaceId || this.inFlightWorkspaceId !== channel) return;
    this.clearInFlight();
    this.refreshQueued = false;
    this.store.applyError(asString(raw.text) || DEFAULT_ERROR_TEXT);
  }

  /** Sends a non-history command through the best available channel (used by
   * the History dock for `load_thread`). The response is NOT broker-owned, so
   * this never touches the in-flight tracker or the epoch guard. Returns
   * true while the process-wide bridge is available. */
  sendCommandOnChannel(command: string, value: string, preferredWorkspaceId = ''): boolean {
    if (this.disposed) return false;
    const channel = this.pickChannel(preferredWorkspaceId);
    if (channel === GLOBAL_CHANNEL) this.host.sendGlobalCommand(command, value);
    else this.host.bridgeFor(channel)?.sendAgentCommand(command, value);
    return true;
  }

  dispose(): void {
    this.disposed = true;
    this.clearInFlight();
    if (this.invalidationTimer !== null) {
      clearTimeout(this.invalidationTimer);
      this.invalidationTimer = null;
    }
  }

  // --- Wave machinery -----------------------------------------------------------

  private startWave(channel: string): void {
    this.retried = false;
    if (!channel) {
      // No live workspace can carry the request: keep the cached list.
      this.store.applyUnavailable();
      return;
    }
    this.sendRequest(channel);
  }

  private startQueuedWave(): void {
    if (!this.refreshQueued || this.disposed) return;
    this.refreshQueued = false;
    this.startWave(this.pickChannel(''));
  }

  private retryDirtyIfPossible(): void {
    if (this.disposed || this.inFlightWorkspaceId) return;
    if (!this.store.getState().dirty) return;
    this.startWave(this.pickChannel(''));
  }

  private sendRequest(channel: string): void {
    if (channel === GLOBAL_CHANNEL) {
      const requestId = this.beginRequest(channel);
      this.host.sendGlobalCommand('history', undefined, requestId);
      return;
    }
    const bridge = this.host.bridgeFor(channel);
    if (!bridge) {
      // The channel vanished between selection and send: treat as a lost
      // carrier and retry once elsewhere, otherwise report unavailability.
      if (!this.retried) {
        this.retried = true;
        const fallback = this.pickChannelExcept(channel);
        if (fallback) {
          this.sendRequest(fallback);
          return;
        }
      }
      this.store.applyUnavailable();
      return;
    }
    const requestId = this.beginRequest(channel);
    bridge.sendAgentCommand('history', undefined, requestId);
  }

  private beginRequest(channel: string): string {
    const requestId = this.nextId();
    this.epoch++;
    this.inFlightWorkspaceId = channel;
    this.inFlightRequestId = requestId;
    this.inFlightEpoch = this.epoch;
    const status = this.store.getState().threads.length > 0 ? 'refreshing' : 'initial-loading';
    this.store.applyLoading(status, channel);
    const epoch = this.epoch;
    this.timer = setTimeout(() => this.onTimeout(epoch), this.timeoutMs);
    return requestId;
  }

  private onTimeout(epoch: number): void {
    this.timer = null;
    // Stale timer callbacks from a previous attempt die on the epoch guard.
    if (this.disposed || !this.inFlightWorkspaceId || epoch !== this.inFlightEpoch) return;
    const failedChannel = this.inFlightWorkspaceId;
    this.clearInFlight();
    if (!this.retried) {
      this.retried = true;
      // Prefer a different channel; the failed one is still a valid retry
      // target when it is the only live workspace.
      const channel = this.pickChannelExcept(failedChannel)
        || (failedChannel === GLOBAL_CHANNEL || this.channels.has(failedChannel) ? failedChannel : '');
      if (channel) {
        this.sendRequest(channel);
        return;
      }
    }
    this.store.applyError(TIMEOUT_ERROR_TEXT);
  }

  private clearInFlight(): void {
    this.inFlightWorkspaceId = '';
    this.inFlightRequestId = '';
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  // --- Channel selection ------------------------------------------------------

  private pickChannel(preferred: string): string {
    return this.channels.pick(preferred) || GLOBAL_CHANNEL;
  }

  private pickChannelExcept(excluded: string): string {
    return this.channels.pickExcept(excluded)
      || (excluded === GLOBAL_CHANNEL ? '' : GLOBAL_CHANNEL);
  }

  private nextId(): string {
    this.requestCounter += 1;
    return `h-${this.requestInstanceId}-${this.requestCounter}`;
  }
}
