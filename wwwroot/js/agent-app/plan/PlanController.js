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
// that is never persisted. The preference defaults to visible; Shell applies a
// temporary narrow override so an empty card never covers the conversation
// unprompted. The user hides/shows it through the toolbar
// toggle. While hidden, an arriving active plan raises the unread dot on the
// toggle instead of interrupting; showing the card clears it. The width is a
// fixed CSS token; there is no resizer.
import { planStatusClass, planStatusLabel, planStatusMarker } from '../core/plan.js';
import { isReactUiEnabled } from '../core/flags.js';
import { createIslandLoader } from '../core/islandHost.js';
const EMPTY_PLAN = { active: false, runId: '', entries: [], fallbackText: '' };
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
    // preferredVisible is workspace-local user intent. narrowOverride is a
    // temporary responsive value: null means wide mode, boolean means narrow.
    preferredVisible = true;
    narrowOverride = null;
    unread = false;
    lastPlanRef = EMPTY_PLAN;
    // React migration: the plan-panel content is React-owned once the first
    // plan event arrives in react mode; visibility, the unread dot and the
    // narrow-mode rules above stay with this controller (different subtrees).
    reactPlanEnabled = false;
    planIsland = null;
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
        this.on(this.planToggle, 'click', () => this.requestVisible(!this.effectiveVisible()));
        this.reactPlanEnabled = isReactUiEnabled();
        // The initial empty state is always legacy-rendered: React loads lazily
        // on the first plan event, and its first commit replaces this content.
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
        this.planIsland?.dispose();
        this.planIsland = null;
        this.panel = null;
    }
    // --- Visibility (visible / hidden) -------------------------------------------
    /** Shell layout seam for the narrow mutual-exclusion rule and Esc close. */
    isVisible() {
        return this.effectiveVisible();
    }
    /** User or Shell request: wide changes the workspace preference, narrow
     * changes only the temporary responsive override. */
    requestVisible(visible) {
        if (this.narrowOverride === null)
            this.setPreferredVisible(visible);
        else
            this.setNarrowOverride(visible);
    }
    closeCard() {
        this.requestVisible(false);
    }
    /** Registry fans Shell's one responsive mode out to every workspace. */
    applyNarrow(narrow) {
        if (narrow) {
            // A repeated broadcast must preserve a user's temporary choice.
            if (this.narrowOverride !== null)
                return;
            this.setNarrowOverride(false);
            return;
        }
        if (this.narrowOverride === null)
            return;
        this.setNarrowOverride(null);
    }
    focusToggle() {
        this.planToggle?.focus();
    }
    applyVisibility() {
        // Becoming visible is the "user saw it" signal that clears the unread dot.
        const visible = this.effectiveVisible();
        if (visible)
            this.unread = false;
        if (this.planCard)
            this.planCard.hidden = !visible;
        if (this.planToggle) {
            this.planToggle.setAttribute('aria-expanded', String(visible));
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
        if (!this.effectiveVisible() && plan.active)
            this.unread = true;
        if (this.reactPlanEnabled && !this.planIsland?.hasFailed()) {
            this.renderPlanReact(plan);
        }
        else {
            this.renderPlan(plan);
        }
        this.applyVisibility();
    }
    renderPlanReact(plan) {
        this.planIsland ??= createIslandLoader({
            name: 'plan-card',
            load: async () => {
                const mod = await import('./planIsland.js');
                return (host) => mod.mountPlanIsland(host);
            },
            // React takes ownership of the template-owned plan panel itself; its
            // first commit replaces the legacy-rendered content atomically.
            createHost: () => this.planPanel,
            onLoadFailed: (props) => {
                if (this.panel)
                    this.renderPlan(props);
            }
        });
        this.planIsland.render(plan);
    }
    effectiveVisible() {
        return this.narrowOverride ?? this.preferredVisible;
    }
    setPreferredVisible(visible) {
        if (this.preferredVisible === visible)
            return;
        const previous = this.effectiveVisible();
        this.preferredVisible = visible;
        this.syncEffectiveVisibility(previous);
    }
    setNarrowOverride(visible) {
        if (this.narrowOverride === visible)
            return;
        const previous = this.effectiveVisible();
        this.narrowOverride = visible;
        this.syncEffectiveVisibility(previous);
    }
    syncEffectiveVisibility(previous) {
        this.applyVisibility();
        if (this.effectiveVisible() !== previous)
            this.callbacks.onVisibilityChanged?.();
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
