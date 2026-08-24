// UsagePanelController.ts — owns the global Usage panel island (singleton).
//
// Mirrors HistoryDockController's shape at a fraction of the size: entry.ts
// mounts it once into the app container; the registry provides the host seam
// (UsageStore reads + broker actions). The React island renders the Dialog;
// this controller only re-renders on store changes and owns the island's
// lifecycle. Opening the panel is the registry's job (PSX 设置 →
// setPanelOpen(true) + a cached usage_report / config_report).

import type { SettingsSection, UsageState } from '../contracts/agent-usage.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import type { UsagePanelProps } from './UsagePanel.js';
import type { DshRegistryKey } from './settingsRegistry.js';

/** Everything the panel needs from the registry (usage store + broker). */
export interface UsagePanelHost {
  getState(): UsageState;
  subscribe(listener: () => void): () => void;
  /** force=false → cached (open/retry); force=true → Refresh button. */
  requestUsage(force: boolean): void;
  requestConfig(force: boolean): void;
  setDisplayName(name: string): void;
  setAvatar(base64Png: string): void;
  setActiveTab(section: SettingsSection): void;
  setSettingsDraft(registry: DshRegistryKey): void;
  applyRegistry(registry: DshRegistryKey): void;
  setLocale(mode: string): void;
  close(): void;
}

export class UsagePanelController {
  private readonly host: UsagePanelHost;

  private panelHost: HTMLElement | null = null;
  private unsubscribe: (() => void) | null = null;
  private usageIsland: IslandLoader<UsagePanelProps> | null = null;

  constructor(host: UsagePanelHost) {
    this.host = host;
  }

  mount(parent: HTMLElement): void {
    if (this.panelHost || !parent) return;
    const panelHost = document.createElement('div');
    panelHost.dataset.role = 'usage-panel-host';
    panelHost.style.display = 'contents';
    parent.appendChild(panelHost);
    this.panelHost = panelHost;
    this.unsubscribe = this.host.subscribe(() => this.render());
    this.render();
  }

  dispose(): void {
    if (this.unsubscribe) {
      this.unsubscribe();
      this.unsubscribe = null;
    }
    this.usageIsland?.dispose();
    this.usageIsland = null;
    this.panelHost?.remove();
    this.panelHost = null;
  }

  private render(): void {
    const panelHost = this.panelHost;
    if (!panelHost) return;

    this.usageIsland ??= createIslandLoader<UsagePanelProps>({
      name: 'usage-panel',
      load: async () => {
        const mod = await import('./usageIsland.js');
        return (host, reportFailure) => mod.mountUsageIsland(host, reportFailure);
      },
      host: panelHost
    });

    this.usageIsland.render({
      state: this.host.getState(),
      onOpenChange: (open) => {
        if (!open) this.host.close();
      },
      onRefresh: () => this.host.requestUsage(true),
      onRetry: () => this.host.requestUsage(false),
      onRequestConfig: (force) => this.host.requestConfig(force),
      onSelectSection: (section) => this.host.setActiveTab(section),
      onSetDisplayName: (name) => this.host.setDisplayName(name),
      onSetAvatar: (base64Png) => this.host.setAvatar(base64Png),
      onSetSettingsDraft: (registry) => this.host.setSettingsDraft(registry),
      onApplyRegistry: (registry) => this.host.applyRegistry(registry),
      onSetLocale: (mode) => this.host.setLocale(mode),
      onRestoreFocus: () => {
        document.querySelector<HTMLElement>('[data-role="app-settings-toggle"]')?.focus();
      }
    });
  }
}
