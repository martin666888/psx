// UsageStore.ts — process-global owner of the user profile + last usage/config
// reports.
//
// Pure state + notify, mirroring AgentHistoryStore. The UsageRequestBroker
// writes through the apply* methods; the footer and Usage panel subscribe.
// Profile updates apply by monotonic revision so a late broadcast can never
// overwrite a newer local value.

import type {
  AgentUserProfile,
  ConfigReport,
  DshRegistryKey,
  SettingsSection,
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
  applyProfile(
    profile: AgentUserProfile,
    options?: { saving?: boolean; clearError?: boolean }
  ): void {
    const acceptedProfile = profile.revision < this.state.profile.revision
      ? this.state.profile
      : profile;
    this.set({
      ...this.state,
      profile: acceptedProfile,
      profileError: options?.clearError ? '' : this.state.profileError,
      profileSaving: options?.saving ?? false
    });
  }

  beginProfileSave(): void {
    this.set({ ...this.state, profileSaving: true, profileError: '' });
  }

  /** `key` is a fixed settings-locale key; backend sentences never cross. */
  applyProfileError(key: string, options?: { saving?: boolean }): void {
    this.set({
      ...this.state,
      profileError: key,
      profileSaving: options?.saving ?? false
    });
  }

  applyUsageLoading(): void {
    this.set({ ...this.state, status: 'loading', errorKey: '' });
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
      errorKey: ''
    });
  }

  applyUsageError(key: string): void {
    this.set({ ...this.state, status: 'error', errorKey: key });
  }

  applyConfigLoading(): void {
    this.set({ ...this.state, configStatus: 'loading', configErrorKey: '' });
  }

  applyConfigReport(report: ConfigReport, generatedAt: string): void {
    this.set({
      ...this.state,
      configReport: report,
      configGeneratedAt: generatedAt,
      configStatus: 'idle',
      configErrorKey: '',
      configLoadedOnce: true
    });
  }

  applyConfigError(key: string): void {
    this.set({
      ...this.state,
      configStatus: 'error',
      configErrorKey: key,
      configLoadedOnce: true
    });
  }

  setActiveTab(tab: SettingsSection): void {
    if (this.state.activeTab === tab) return;
    this.set({ ...this.state, activeTab: tab });
  }

  setPanelOpen(open: boolean): void {
    if (this.state.panelOpen === open) return;
    this.set({ ...this.state, panelOpen: open });
    document.dispatchEvent(new CustomEvent('psx-settings-state', { detail: { open } }));
  }

  setSettingsDraft(draft: DshRegistryKey): void {
    if (this.state.settingsDraft === draft && !this.state.settingsError) return;
    this.set({ ...this.state, settingsDraft: draft, settingsError: '' });
  }

  applyAppSettings(snapshot: {
    revision: number;
    dshRegistry: DshRegistryKey;
    draft?: DshRegistryKey;
    error: string;
    languageError?: string;
    localeMode?: string;
    resolvedLocale?: string;
  }): void {
    this.set({
      ...this.state,
      settingsRevision: snapshot.revision,
      dshRegistry: snapshot.dshRegistry,
      settingsDraft: snapshot.draft ?? this.state.settingsDraft,
      settingsError: snapshot.error,
      localeMode: snapshot.localeMode ?? this.state.localeMode,
      resolvedLocale: snapshot.resolvedLocale ?? this.state.resolvedLocale,
      languageError: snapshot.languageError ?? ''
    });
  }

  private set(next: UsageState): void {
    this.state = next;
    for (const listener of this.listeners) listener(next);
  }
}
