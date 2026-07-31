// UsageStore.ts — process-global owner of the user profile + last usage report.
//
// Pure state + notify, mirroring AgentHistoryStore. The UsageRequestBroker
// writes through the apply* methods; the footer and Usage panel subscribe.
// Profile updates apply by monotonic revision so a late broadcast can never
// overwrite a newer local value.

import type {
  AgentUserProfile,
  UsageCompleteness,
  UsageListener,
  UsageReport,
  UsageState
} from '../contracts/agent-usage.js';
import { createInitialUsageState } from '../contracts/agent-usage.js';

export class UsageStore {
  private state: UsageState = createInitialUsageState();
  private readonly listeners = new Set<UsageListener>();

  getState(): UsageState {
    return this.state;
  }

  subscribe(listener: UsageListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /** Apply a profile snapshot, but only if its revision is not older than the
   * one already shown (monotonic guard for out-of-order broadcasts). */
  applyProfile(profile: AgentUserProfile): void {
    if (profile.revision < this.state.profile.revision) return;
    this.set({ ...this.state, profile });
  }

  applyUsageLoading(): void {
    this.set({ ...this.state, status: 'loading', errorText: '' });
  }

  applyUsageReport(
    report: UsageReport,
    completeness: UsageCompleteness,
    generatedAt: string,
    timezone: string
  ): void {
    this.set({
      ...this.state,
      report,
      completeness,
      generatedAt,
      timezone,
      status: 'idle',
      errorText: ''
    });
  }

  applyUsageError(text: string): void {
    this.set({ ...this.state, status: 'error', errorText: text });
  }

  setPanelOpen(open: boolean): void {
    if (this.state.panelOpen === open) return;
    this.set({ ...this.state, panelOpen: open });
  }

  private set(next: UsageState): void {
    this.state = next;
    for (const listener of this.listeners) listener(next);
  }
}
