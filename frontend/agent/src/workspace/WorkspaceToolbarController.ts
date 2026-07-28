// WorkspaceToolbarController.ts — the single owner of the toolbar island.
//
// Aggregates the toolbar view-model: the Session/History/Plan controllers
// push their slices through the set* APIs and this controller is the only
// setElement writer for [data-role="toolbar-host"]. It owns no domain state
// of its own — every slice has exactly one authoritative source elsewhere
// (session projection, global dock, workspace PlanController).

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';
import type { RefObject } from 'react';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import type { SessionMetaProps } from './SessionToolbar.js';
import type { WorkspaceToolbarProps } from './WorkspaceToolbarView.js';

export interface WorkspaceToolbarHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
}

export class WorkspaceToolbarController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: WorkspaceToolbarHost;

  private toolbarHost: HTMLElement | null = null;
  private island: IslandLoader<WorkspaceToolbarProps> | null = null;

  private session: SessionMetaProps | null = null;
  private historyOpen = false;
  private planVisible = true;
  private planUnread = false;
  private onHistoryToggle: () => void = () => {};
  private onPlanToggle: () => void = () => {};

  // Hand-made ref objects (React assigns .current): the Shell Esc path and
  // PlanController.focusToggle reach the rendered buttons through these
  // without any DOM query.
  readonly historyToggleRef: RefObject<HTMLButtonElement | null> = { current: null };
  readonly planToggleRef: RefObject<HTMLButtonElement | null> = { current: null };

  constructor(workspaceId: string, host: WorkspaceToolbarHost) {
    this.workspaceId = workspaceId;
    this.host = host;
  }

  mount(): void {
    this.toolbarHost =
      this.host
        .getPanel(this.workspaceId)
        ?.querySelector<HTMLElement>('[data-role="toolbar-host"]') ?? null;
    this.render();
  }

  // Domain state never arrives through the event stream; the owning
  // controllers push their slices via the set* APIs below.
  update(_event: AgentWorkspaceEvent, _state: AgentWorkspaceState): void {}

  dispose(): void {
    this.island?.dispose();
    this.island = null;
    this.toolbarHost = null;
  }

  // --- push APIs (one per state owner) --------------------------------------

  setSessionProps(session: SessionMetaProps): void {
    this.session = session;
    this.render();
  }

  setHistoryOpen(open: boolean): void {
    if (this.historyOpen === open) return;
    this.historyOpen = open;
    this.render();
  }

  setPlanState(visible: boolean, unread: boolean): void {
    if (this.planVisible === visible && this.planUnread === unread) return;
    this.planVisible = visible;
    this.planUnread = unread;
    this.render();
  }

  setToggleHandlers(onHistoryToggle: () => void, onPlanToggle: () => void): void {
    this.onHistoryToggle = onHistoryToggle;
    this.onPlanToggle = onPlanToggle;
  }

  // --- focus commands (Shell Esc return path) --------------------------------

  focusHistoryToggle(): void {
    this.historyToggleRef.current?.focus();
  }

  focusPlanToggle(): void {
    this.planToggleRef.current?.focus();
  }

  private render(): void {
    const host = this.toolbarHost;
    if (!host) return;
    this.island ??= createIslandLoader<WorkspaceToolbarProps>({
      name: 'workspace-toolbar',
      load: async () => {
        const mod = await import('./toolbarIsland.js');
        return (islandHost, reportFailure) =>
          mod.mountToolbarIsland(islandHost, reportFailure);
      },
      host
    });
    this.island.render({
      session: this.session,
      history: {
        open: this.historyOpen,
        onToggle: () => this.onHistoryToggle(),
        toggleRef: this.historyToggleRef
      },
      plan: {
        visible: this.planVisible,
        unread: this.planUnread,
        onToggle: () => this.onPlanToggle(),
        toggleRef: this.planToggleRef
      }
    });
  }
}
