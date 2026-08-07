// PaneLayoutController.js — the neutral pane layout root shared by Terminal
// and Agent workspaces (split-pane Phase 0).
//
// Owns the .workspace-pane slots inside #workspace-panes. This controller is
// deliberately terminal/agent-agnostic and lives in the always-loaded webview
// layer (not the lazily imported Agent chunk): a Terminal-only split must
// work without ever loading React. Phase 0 keeps exactly one pane and only
// establishes the seam — TerminalManager keeps writing into
// #terminal-container and the Agent app keeps its overlay layer, both now
// inside a pane instead of directly under <body>. Phase 1 will drive pane
// creation, ratio resizing and removal from WorkspaceLayoutService snapshots.

export class PaneLayoutController {
    constructor(root) {
        if (!root) throw new Error('PaneLayoutController requires the #workspace-panes root');
        this.root = root;
    }

    get paneCount() {
        return this.root.querySelectorAll('.workspace-pane').length;
    }

    paneAt(index) {
        return this.root.querySelectorAll('.workspace-pane')[index] ?? null;
    }

    paneById(paneId) {
        for (const pane of this.root.querySelectorAll('.workspace-pane')) {
            if (pane.dataset.paneId === paneId) return pane;
        }
        return null;
    }
}
