// WorkspaceChannelSelector.ts — shared live-workspace channel picker.
//
// Global brokers (History, Usage) send commands through one live Agent
// workspace's scoped bridge. This owns the alive set + activation order and the
// channel-priority policy so both brokers select identically: originating
// workspace (alive) -> active Agent workspace -> most recently activated live
// workspace -> any live workspace. Busy and transcript-only workspaces are
// valid carriers; closing/closed/unknown are not.

import type { AgentBridgePort } from '../contracts/bridge-port.js';

export interface WorkspaceChannelHost {
  isAlive(workspaceId: string): boolean;
  /** The active Agent workspace, or '' when a terminal/nothing is active. */
  activeAgentWorkspace(): string;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
}

export class WorkspaceChannelSelector {
  private readonly host: WorkspaceChannelHost;
  private readonly alive = new Set<string>();
  // Most-recently-activated last; register order otherwise.
  private readonly activationOrder: string[] = [];

  constructor(host: WorkspaceChannelHost) {
    this.host = host;
  }

  /** Returns true when the workspace was newly registered. */
  register(workspaceId: string): boolean {
    const id = String(workspaceId || '');
    if (!id || this.alive.has(id)) return false;
    this.alive.add(id);
    this.activationOrder.push(id);
    return true;
  }

  /** Moves an alive workspace to most-recently-activated. No-op otherwise. */
  activate(workspaceId: string): boolean {
    const id = String(workspaceId || '');
    if (!this.alive.has(id)) return false;
    const index = this.activationOrder.indexOf(id);
    if (index >= 0) this.activationOrder.splice(index, 1);
    this.activationOrder.push(id);
    return true;
  }

  /** Returns true when the workspace was actually removed. */
  unregister(workspaceId: string): boolean {
    const id = String(workspaceId || '');
    if (!this.alive.delete(id)) return false;
    const index = this.activationOrder.indexOf(id);
    if (index >= 0) this.activationOrder.splice(index, 1);
    return true;
  }

  has(workspaceId: string): boolean {
    return this.alive.has(workspaceId);
  }

  get size(): number {
    return this.alive.size;
  }

  bridgeFor(workspaceId: string): AgentBridgePort | null {
    return this.host.bridgeFor(workspaceId);
  }

  pick(preferred: string): string {
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

  pickExcept(excluded: string): string {
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
