// PlanController.ts — the workspace-local Plan card owner.
//
// The Plan card is the first tenant of the generic context-card column
// (data-role="context-cards") inside every workspace panel; future cards will
// reuse the same host, but there is deliberately no registry abstraction yet.
// Plan entries render purely from the reduced AgentWorkspaceState (the
// reducer folds plan_update / agent_thread_loaded / agent_cleared), and the
// panel DOM survives Tab hide/show untouched.
//
// Visibility is two-state (visible / hidden), workspace-local runtime state
// that is never persisted. Wide mode (>=1080px) shows the card by default — a
// small, content-height card below the toolbar, empty state included — while
// compact mode starts with the drawer closed so an empty card never covers
// the conversation unprompted. The user hides/shows it through the toolbar
// toggle. While hidden, an arriving active plan raises the unread dot on the
// toggle instead of interrupting; showing the card clears it. The width is a
// fixed CSS token; there is no resizer.
import { planStatusClass, planStatusLabel, planStatusMarker } from '../core/plan.js';
const EMPTY_PLAN = { active: false, runId: '', entries: [], fallbackText: '' };
// Same breakpoint as AgentShellLayoutController: wide shows the card by
// default, compact keeps the drawer closed until the user asks for it.
const WIDE_QUERY = '(min-width: 1080px)';
function role(panel, name) {
    return panel.querySelector('[data-role="' + name + '"]');
}
export class PlanController {
    workspaceId;
    host;
    callbacks;
    panel = null;
    planCard = null;
    planPanel = null;
    planToggle = null;
    planToggleUnread = null;
    visible = true;
    unread = false;
    lastPlanRef = EMPTY_PLAN;
    cleanup = [];
    constructor(workspaceId, host, callbacks) {
        this.workspaceId = workspaceId;
        this.host = host;
        this.callbacks = callbacks ?? {};
    }
    mount() {
        const panel = this.host.getPanel(this.workspaceId);
        if (!panel)
            return;
        this.panel = panel;
        this.planCard = role(panel, 'plan-card');
        this.planPanel = role(panel, 'plan-panel');
        this.planToggle = role(panel, 'plan-toggle');
        this.planToggleUnread = role(panel, 'plan-toggle-unread');
        this.visible = this.readDefaultVisible();
        this.on(this.planToggle, 'click', () => this.setVisible(!this.visible));
        this.renderPlan(EMPTY_PLAN);
        this.applyVisibility();
    }
    update(event, state) {
        if (!this.panel)
            return;
        switch (event.type) {
            case 'plan_update':
            case 'agent_thread_loaded':
            case 'agent_cleared':
                this.applyPlan(state);
                break;
            default:
                break;
        }
    }
    dispose() {
        for (const off of this.cleanup.splice(0))
            off();
        this.panel = null;
    }
    // --- Visibility (visible / hidden) -------------------------------------------
    readDefaultVisible() {
        try {
            return window.matchMedia(WIDE_QUERY).matches;
        }
        catch {
            // matchMedia can be unavailable in constrained WebView profiles.
            return true;
        }
    }
    /** Shell layout seam for the compact one-drawer rule and Esc close. */
    isVisible() {
        return this.visible;
    }
    setVisible(visible) {
        if (this.visible === visible)
            return;
        this.visible = visible;
        this.applyVisibility();
        this.callbacks.onVisibilityChanged?.();
    }
    closeCard() {
        this.setVisible(false);
    }
    focusToggle() {
        this.planToggle?.focus();
    }
    applyVisibility() {
        // Becoming visible is the "user saw it" signal that clears the unread dot.
        if (this.visible)
            this.unread = false;
        if (this.planCard)
            this.planCard.hidden = !this.visible;
        if (this.planToggle) {
            this.planToggle.setAttribute('aria-expanded', String(this.visible));
            // The explicit aria-label overrides any inner span's label, so the
            // unread state must be part of the button's own accessible name.
            this.planToggle.setAttribute('aria-label', this.unread ? 'Toggle Plan card, plan updated' : 'Toggle Plan card');
        }
        if (this.planToggleUnread)
            this.planToggleUnread.hidden = !this.unread;
    }
    // --- Plan rendering ---------------------------------------------------------
    /** Renders the reduced plan when it actually changed; while hidden an
     * active-plan update raises the unread dot on the toolbar toggle. */
    applyPlan(state) {
        const plan = state.inspector.plan;
        if (plan === this.lastPlanRef)
            return;
        this.lastPlanRef = plan;
        if (!this.visible && plan.active)
            this.unread = true;
        this.renderPlan(plan);
        this.applyVisibility();
    }
    renderPlan(plan) {
        if (!this.planPanel)
            return;
        this.planPanel.innerHTML = '';
        if (!plan.active) {
            const empty = document.createElement('div');
            empty.className = 'agent-plan-empty';
            empty.textContent = 'No active plan';
            this.planPanel.appendChild(empty);
            return;
        }
        if (plan.entries.length === 0) {
            const fallback = document.createElement('pre');
            fallback.className = 'agent-plan-fallback';
            fallback.textContent = plan.fallbackText || 'No plan items.';
            this.planPanel.appendChild(fallback);
            return;
        }
        const list = document.createElement('ol');
        list.className = 'agent-plan-list';
        plan.entries.forEach((entry) => {
            const item = document.createElement('li');
            item.className = 'agent-plan-item ' + planStatusClass(entry.status);
            item.setAttribute('aria-label', planStatusLabel(entry.status) + ': ' + entry.content);
            if (entry.priority)
                item.dataset.priority = entry.priority;
            const marker = document.createElement('span');
            marker.className = 'agent-plan-marker';
            marker.textContent = planStatusMarker(entry.status);
            const content = document.createElement('span');
            content.className = 'agent-plan-content';
            content.textContent = entry.content;
            item.appendChild(marker);
            item.appendChild(content);
            list.appendChild(item);
        });
        this.planPanel.appendChild(list);
    }
    on(node, type, handler) {
        if (!node)
            return;
        node.addEventListener(type, handler);
        this.cleanup.push(() => node.removeEventListener(type, handler));
    }
}
