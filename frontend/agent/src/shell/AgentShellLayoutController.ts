// AgentShellLayoutController.ts — shell-level responsive layout coordination.
//
// Owns the ≥1080px / <1080px mode switch (wide: History dock + Plan overlay;
// compact: left/right drawers) and the one-drawer-at-a-time rule. The rule
// needs a tiny bit of coordination because History is a global controller
// while Plan cards are per-workspace: this controller subscribes to both
// through one flat host seam and translates between them — deliberately no
// bigger abstraction. It also owns drawer keyboard accessibility (Esc closes,
// focus returns to the trigger) and the history trigger's aria-expanded
// state; the trigger element itself is dock-owned chrome.
//
// Conversation layout is never touched: drawers float above the canvas, so
// the window-center reading column holds in both modes.

/** Flat seam the registry builds over the History dock + Plan controllers. */
export interface AgentShellLayoutHost {
  isHistoryOpen(): boolean;
  setHistoryOpen(open: boolean): void;
  onHistoryOpenChanged(listener: (open: boolean) => void): void;
  focusHistorySearch(): void;
  focusHistoryTrigger(): void;
  /** Active workspace's Plan card (drawer mode is about what the user sees). */
  isActivePlanExpanded(): boolean;
  closeActivePlan(): void;
  focusActivePlanEntry(): void;
  onActivePlanExpandedChanged(listener: (expanded: boolean) => void): void;
}

// Wide at or above this width; below it History/Plan become drawers.
const WIDE_QUERY = '(min-width: 1080px)';

export class AgentShellLayoutController {
  private readonly container: HTMLElement;
  private readonly host: AgentShellLayoutHost;
  private mql: MediaQueryList | null = null;
  private compact = false;
  private readonly cleanup: Array<() => void> = [];
  private readonly onMqlChange = (): void => this.applyMode();

  constructor(container: HTMLElement, host: AgentShellLayoutHost) {
    this.container = container;
    this.host = host;
  }

  mount(): void {
    this.host.onHistoryOpenChanged((open) => this.onHistoryOpenChanged(open));
    this.host.onActivePlanExpandedChanged((expanded) => this.onPlanExpandedChanged(expanded));

    this.mql = window.matchMedia(WIDE_QUERY);
    this.mql.addEventListener?.('change', this.onMqlChange);
    this.applyMode();

    const onKeydown = (event: KeyboardEvent): void => this.onKeydown(event);
    document.addEventListener('keydown', onKeydown);
    this.cleanup.push(() => document.removeEventListener('keydown', onKeydown));
  }

  dispose(): void {
    this.mql?.removeEventListener?.('change', this.onMqlChange);
    for (const off of this.cleanup.splice(0)) off();
  }

  private applyMode(): void {
    this.compact = this.mql ? !this.mql.matches : false;
    this.container.classList.toggle('agent-shell-compact', this.compact);
    // Crossing into drawer mode keeps open state (dock ⇔ drawer is one flag)
    // but enforces the one-drawer rule; History is the global, explicit one
    // and wins the tie.
    if (this.compact && this.host.isHistoryOpen() && this.host.isActivePlanExpanded()) {
      this.host.closeActivePlan();
    }
  }

  private onHistoryOpenChanged(open: boolean): void {
    if (!open) return;
    if (this.compact && this.host.isActivePlanExpanded()) this.host.closeActivePlan();
    // Opening a drawer moves focus into its first interactive control.
    this.host.focusHistorySearch();
  }

  private onPlanExpandedChanged(expanded: boolean): void {
    if (expanded && this.compact && this.host.isHistoryOpen()) {
      this.host.setHistoryOpen(false);
    }
  }

  private onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || !this.compact || event.defaultPrevented) return;
    if (this.host.isHistoryOpen()) {
      this.host.setHistoryOpen(false);
      this.host.focusHistoryTrigger();
    } else if (this.host.isActivePlanExpanded()) {
      this.host.closeActivePlan();
      this.host.focusActivePlanEntry();
    }
  }
}
