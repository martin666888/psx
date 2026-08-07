// AgentShellLayoutController.ts — shell-level responsive layout coordination.
//
// The Shell owns two related responsive rules. Below 1000px of PANE width,
// both context panels temporarily collapse and manually reopened panels are
// exclusive. At wider sizes, History remains a dock only while the full
// reading column fits beside it; otherwise History temporarily yields and the
// conversation keeps its comfortable width. While docked, the reading column
// is centred in the remaining conversation panel. Plan remains an overlay and
// never participates in this calculation. All measurements are pane-relative
// (the agent container), so split panes evaluate against their own width
// instead of the viewport.

/** Flat seam the registry builds over the History dock + Plan controllers. */
export interface AgentShellLayoutHost {
  /** Shell is the single responsive owner; Registry fans the state out. */
  setResponsiveLayout(narrow: boolean, collapseHistoryForReading: boolean): void;
  isHistoryOpen(): boolean;
  closeHistory(): void;
  onHistoryOpenChanged(listener: (open: boolean) => void): void;
  /** Persisted History width is part of the reading-fit calculation. */
  historyWidth(): number;
  onHistoryWidthChanged(listener: (width: number) => void): void;
  focusHistoryToggle(): void;
  /** Active workspace's Plan card (narrow mode is about what the user sees). */
  isActivePlanVisible(): boolean;
  closeActivePlan(): void;
  focusActivePlanToggle(): void;
  onActivePlanVisibilityChanged(listener: (visible: boolean) => void): void;
}

// Narrow breakpoint in pane pixels, mirrored by the shell.css narrow rules.
const SHELL_NARROW_MAX_WIDTH = 999;
const READING_MAX_WIDTH = 920;
// Soft-workbench geometry mirrored from shell.css: the left workbench gutter
// before the dock, the gap between the two panels, and the right pane padding
// the reading column keeps (measured from the pane edge, so it already covers
// the right gutter).
const WORKBENCH_GUTTER = 12;
const PANEL_GAP = 12;
const VIEWPORT_PADDING = 24;

export class AgentShellLayoutController {
  private readonly container: HTMLElement;
  private readonly host: AgentShellLayoutHost;
  private resizeObserver: ResizeObserver | null = null;
  private narrow = false;
  private historyReadingConstrained = false;
  private readonly cleanup: Array<() => void> = [];
  private readonly onResize = (): void => this.applyMode();

  constructor(container: HTMLElement, host: AgentShellLayoutHost) {
    this.container = container;
    this.host = host;
  }

  mount(): void {
    this.host.onHistoryOpenChanged((open) => this.onHistoryOpenChanged(open));
    this.host.onHistoryWidthChanged(() => this.applyMode());
    this.host.onActivePlanVisibilityChanged((visible) => this.onPlanVisibilityChanged(visible));

    // Pane-width measurement: ResizeObserver drives production (pane drags
    // and window resizes alike); the window resize fallback covers
    // environments without ResizeObserver.
    if (typeof ResizeObserver === 'function') {
      this.resizeObserver = new ResizeObserver(this.onResize);
      this.resizeObserver.observe(this.container);
    }
    window.addEventListener('resize', this.onResize);
    this.cleanup.push(() => window.removeEventListener('resize', this.onResize));
    this.applyMode();

    const onKeydown = (event: KeyboardEvent): void => this.onKeydown(event);
    document.addEventListener('keydown', onKeydown);
    this.cleanup.push(() => document.removeEventListener('keydown', onKeydown));
  }

  dispose(): void {
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    for (const off of this.cleanup.splice(0)) off();
  }

  private applyMode(): void {
    const narrow = this.containerWidth() <= SHELL_NARROW_MAX_WIDTH;
    const historyReadingConstrained = !narrow && this.historyWouldSqueezeReading();
    if (this.narrow === narrow && this.historyReadingConstrained === historyReadingConstrained) return;

    this.narrow = narrow;
    this.historyReadingConstrained = historyReadingConstrained;
    // Presentation only: History remains a dock and Plan remains a context
    // card. This class never turns either panel into a drawer.
    this.container.classList.toggle('agent-shell-narrow', narrow);
    this.container.classList.toggle('agent-shell-history-collapsed-for-reading', historyReadingConstrained);
    this.host.setResponsiveLayout(narrow, historyReadingConstrained);
  }

  private historyWouldSqueezeReading(): boolean {
    // Keep the 920px reading column intact whenever it can sit to the right of
    // the dock with the workbench gutter, the inter-panel gap, one inner
    // reading inset and the normal right pane padding. CSS centres that
    // column in the remaining panel with equal insets on both sides.
    const requiredWidth =
      WORKBENCH_GUTTER +
      this.host.historyWidth() +
      PANEL_GAP +
      PANEL_GAP +
      READING_MAX_WIDTH +
      VIEWPORT_PADDING;
    return this.containerWidth() < requiredWidth;
  }

  /** Pane width when measurable; the viewport otherwise (fallback only). */
  private containerWidth(): number {
    if (this.container.clientWidth > 0) return this.container.clientWidth;
    const documentWidth = document.documentElement?.clientWidth ?? 0;
    return documentWidth > 0 ? documentWidth : window.innerWidth;
  }

  private onHistoryOpenChanged(open: boolean): void {
    if (!open) return;
    if (this.narrow && this.host.isActivePlanVisible()) this.host.closeActivePlan();
    // User-initiated History opening inside the reading-constrained range is
    // allowed. CSS then gives it the available width without changing the
    // persisted preference; we do not immediately re-collapse it here.
  }

  private onPlanVisibilityChanged(visible: boolean): void {
    if (visible && this.narrow && this.host.isHistoryOpen()) {
      this.host.closeHistory();
    }
  }

  private onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || !this.narrow || event.defaultPrevented) return;
    if (this.host.isHistoryOpen()) {
      this.host.closeHistory();
      this.host.focusHistoryToggle();
    } else if (this.host.isActivePlanVisible()) {
      this.host.closeActivePlan();
      this.host.focusActivePlanToggle();
    }
  }
}
