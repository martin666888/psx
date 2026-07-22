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
// Wide at or above this width; below it History/Plan become drawers.
const WIDE_QUERY = '(min-width: 1080px)';
export class AgentShellLayoutController {
    container;
    host;
    mql = null;
    compact = false;
    cleanup = [];
    onMqlChange = () => this.applyMode();
    constructor(container, host) {
        this.container = container;
        this.host = host;
    }
    mount() {
        this.host.onHistoryOpenChanged((open) => this.onHistoryOpenChanged(open));
        this.host.onActivePlanExpandedChanged((expanded) => this.onPlanExpandedChanged(expanded));
        this.mql = window.matchMedia(WIDE_QUERY);
        this.mql.addEventListener?.('change', this.onMqlChange);
        this.applyMode();
        const onKeydown = (event) => this.onKeydown(event);
        document.addEventListener('keydown', onKeydown);
        this.cleanup.push(() => document.removeEventListener('keydown', onKeydown));
    }
    dispose() {
        this.mql?.removeEventListener?.('change', this.onMqlChange);
        for (const off of this.cleanup.splice(0))
            off();
    }
    applyMode() {
        this.compact = this.mql ? !this.mql.matches : false;
        this.container.classList.toggle('agent-shell-compact', this.compact);
        // Crossing into drawer mode keeps open state (dock ⇔ drawer is one flag)
        // but enforces the one-drawer rule; History is the global, explicit one
        // and wins the tie.
        if (this.compact && this.host.isHistoryOpen() && this.host.isActivePlanExpanded()) {
            this.host.closeActivePlan();
        }
    }
    onHistoryOpenChanged(open) {
        if (!open)
            return;
        if (this.compact && this.host.isActivePlanExpanded())
            this.host.closeActivePlan();
        // Opening a drawer moves focus into its first interactive control.
        this.host.focusHistorySearch();
    }
    onPlanExpandedChanged(expanded) {
        if (expanded && this.compact && this.host.isHistoryOpen()) {
            this.host.setHistoryOpen(false);
        }
    }
    onKeydown(event) {
        if (event.key !== 'Escape' || !this.compact || event.defaultPrevented)
            return;
        if (this.host.isHistoryOpen()) {
            this.host.setHistoryOpen(false);
            this.host.focusHistoryTrigger();
        }
        else if (this.host.isActivePlanExpanded()) {
            this.host.closeActivePlan();
            this.host.focusActivePlanEntry();
        }
    }
}
