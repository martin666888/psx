// AgentWorkspaceStore.ts — holds one AgentWorkspaceState per live workspace.
//
// Phase 3 makes the store the owner of the reduced state: every decoded
// workspace event is folded through the pure reducer here, producing a new
// immutable state that runs in parallel with the legacy engine. The
// LegacyAgentAdapter still owns all rendering; the store's state is validated
// against the legacy DOM by the equivalence tests until Phase 4 controllers
// read from it.

import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';
import { createInitialWorkspaceState } from '../contracts/workspace-state.js';
import { reduceWorkspaceState, seedIdentityFromCreation } from '../core/reducer.js';

export class AgentWorkspaceStore {
  private readonly states = new Map<string, AgentWorkspaceState>();

  create(workspaceId: string, createdRaw?: RawHostMessage): AgentWorkspaceState {
    const state = seedIdentityFromCreation(createInitialWorkspaceState(workspaceId), createdRaw);
    this.states.set(workspaceId, state);
    return state;
  }

  /** Folds an event into the workspace state via the pure reducer. */
  reduce(workspaceId: string, event: AgentWorkspaceEvent): AgentWorkspaceState | undefined {
    const current = this.states.get(workspaceId);
    if (!current) return undefined;
    const next = reduceWorkspaceState(current, event);
    if (next !== current) this.states.set(workspaceId, next);
    return next;
  }

  get(workspaceId: string): AgentWorkspaceState | undefined {
    return this.states.get(workspaceId);
  }

  delete(workspaceId: string): void {
    this.states.delete(workspaceId);
  }

  has(workspaceId: string): boolean {
    return this.states.has(workspaceId);
  }
}
