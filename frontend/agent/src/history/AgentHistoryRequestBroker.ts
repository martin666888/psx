// AgentHistoryRequestBroker.ts — the single owner of `history` bridge loads.
//
// History data is global, but the bridge command must travel through one live
// Agent workspace. This broker picks the channel, owns the request status
// machine (idle / initial-loading / refreshing / unavailable / error) and
// guards the ID-less agent_threads response with an in-flight workspace match
// plus a monotonic epoch for its own timer callbacks. Every `history` command
// is broker-initiated, so a response with no matching in-flight request is
// dropped.
//
// Rules (approved design):
//   channel priority: originating workspace (alive) → active Agent workspace
//     → most recently activated live workspace → any live workspace. Busy and
//     transcript-only workspaces are valid channels; closing/closed/unknown
//     are not.
//   no channel: no bridge message, cached list kept, status 'unavailable'.
//   carrier closed mid-request: end the load, mark dirty, retry once on
//     another channel; without any channel go 'unavailable'. Dirty loads are
//     retried when a workspace is created or activated.
//   one 10s timeout per attempt, one retry per wave, then 'error'.
//   invalidation coalescing: a broadcast wave (one message per workspace)
//     collapses into a single refresh; an invalidation arriving mid-flight
//     queues at most one follow-up after the current response lands.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { RawHostMessage } from '../contracts/host-events.js';
import { AgentHistoryStore, normalizeHistoryThreads } from './AgentHistoryStore.js';

/** The registry-facing view of the live workspace set the broker needs. */
export interface AgentHistoryBrokerHost {
  isAlive(workspaceId: string): boolean;
  /** The active Agent workspace, or '' when a terminal/nothing is active. */
  activeAgentWorkspace(): string;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
}

export interface AgentHistoryRequestBrokerOptions {
  timeoutMs?: number;
}

const DEFAULT_TIMEOUT_MS = 10000;
const TIMEOUT_ERROR_TEXT = 'Loading Agent thread history timed out.';
const DEFAULT_ERROR_TEXT = 'Unable to load Agent thread history.';

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

export class AgentHistoryRequestBroker {
  private readonly host: AgentHistoryBrokerHost;
  private readonly store: AgentHistoryStore;
  private readonly timeoutMs: number;

  private readonly alive = new Set<string>();
  // Most-recently-activated last; register order otherwise.
  private readonly activationOrder: string[] = [];

  private inFlightWorkspaceId = '';
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
  }

  // --- Workspace registry hooks ---------------------------------------------

  registerWorkspace(workspaceId: string): void {
    const id = String(workspaceId || '');
    if (!id || this.alive.has(id)) return;
    this.alive.add(id);
    this.activationOrder.push(id);
    this.retryDirtyIfPossible();
  }

  activateWorkspace(workspaceId: string): void {
    const id = String(workspaceId || '');
    if (!this.alive.has(id)) return;
    const index = this.activationOrder.indexOf(id);
    if (index >= 0) this.activationOrder.splice(index, 1);
    this.activationOrder.push(id);
    this.retryDirtyIfPossible();
  }

  unregisterWorkspace(workspaceId: string): void {
    const id = String(workspaceId || '');
    if (!this.alive.delete(id)) return;
    const index = this.activationOrder.indexOf(id);
    if (index >= 0) this.activationOrder.splice(index, 1);

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
    if (this.alive.size === 0) this.store.applyUnavailable();
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
    if (!this.alive.has(senderWorkspaceId) || !this.host.isAlive(senderWorkspaceId)) return;
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

  /** agent_threads response: accepted only from the in-flight channel. Every
   * `history` command is broker-initiated, so a response with no in-flight
   * request (or from a stale/closed channel) is dropped. */
  handleThreads(workspaceId: string, raw: RawHostMessage): void {
    if (this.disposed || !this.alive.has(workspaceId)) return;
    if (!this.inFlightWorkspaceId || this.inFlightWorkspaceId !== workspaceId) return;
    this.clearInFlight();
    this.store.applyThreads(normalizeHistoryThreads(raw.threads));
    this.startQueuedWave();
  }

  /** agent_history_error response: same acceptance rules as agent_threads. */
  handleHistoryError(workspaceId: string, raw: RawHostMessage): void {
    if (this.disposed || !this.alive.has(workspaceId)) return;
    if (!this.inFlightWorkspaceId || this.inFlightWorkspaceId !== workspaceId) return;
    this.clearInFlight();
    this.refreshQueued = false;
    this.store.applyError(asString(raw.text) || DEFAULT_ERROR_TEXT);
  }

  /** Sends a non-history command through the best available channel (used by
   * the History dock for `load_thread`). The response is NOT broker-owned, so
   * this never touches the in-flight tracker or the epoch guard. Returns
   * false when no live workspace can carry the command. */
  sendCommandOnChannel(command: string, value: string, preferredWorkspaceId = ''): boolean {
    if (this.disposed) return false;
    const channel = this.pickChannel(preferredWorkspaceId);
    if (!channel) return false;
    this.host.bridgeFor(channel)?.sendAgentCommand(command, value);
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
    this.epoch++;
    this.inFlightWorkspaceId = channel;
    this.inFlightEpoch = this.epoch;
    const status = this.store.getState().threads.length > 0 ? 'refreshing' : 'initial-loading';
    this.store.applyLoading(status, channel);
    bridge.sendAgentCommand('history');
    const epoch = this.epoch;
    this.timer = setTimeout(() => this.onTimeout(epoch), this.timeoutMs);
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
        || (this.alive.has(failedChannel) ? failedChannel : '');
      if (channel) {
        this.sendRequest(channel);
        return;
      }
    }
    this.store.applyError(TIMEOUT_ERROR_TEXT);
  }

  private clearInFlight(): void {
    this.inFlightWorkspaceId = '';
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  // --- Channel selection ------------------------------------------------------

  private pickChannel(preferred: string): string {
    if (preferred && this.alive.has(preferred) && this.host.isAlive(preferred)) return preferred;
    const active = this.host.activeAgentWorkspace();
    if (active && this.alive.has(active) && this.host.isAlive(active)) return active;
    for (let index = this.activationOrder.length - 1; index >= 0; index--) {
      const id = this.activationOrder[index];
      if (this.alive.has(id) && this.host.isAlive(id)) return id;
    }
    for (const id of this.alive) {
      if (this.host.isAlive(id)) return id;
    }
    return '';
  }

  private pickChannelExcept(excluded: string): string {
    const active = this.host.activeAgentWorkspace();
    if (active && active !== excluded && this.alive.has(active) && this.host.isAlive(active)) return active;
    for (let index = this.activationOrder.length - 1; index >= 0; index--) {
      const id = this.activationOrder[index];
      if (id !== excluded && this.alive.has(id) && this.host.isAlive(id)) return id;
    }
    for (const id of this.alive) {
      if (id !== excluded && this.host.isAlive(id)) return id;
    }
    return '';
  }
}
