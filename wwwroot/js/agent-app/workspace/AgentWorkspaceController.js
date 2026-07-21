// AgentWorkspaceController.ts — owns a single Agent workspace.
//
// Phase 2 delegates every content event to the LegacyAgentAdapter, which drives
// the legacy AgentThreadManager for this workspace. The controller holds the
// workspace's state slice and its FeatureController set (a single legacy
// feature today; Phase 4 splits it into Session/Inspector/Composer/Decision/
// Timeline controllers).
export class AgentWorkspaceController {
    workspaceId;
    state;
    features;
    disposed = false;
    constructor(workspaceId, state, features) {
        this.workspaceId = workspaceId;
        this.state = state;
        this.features = features;
    }
    mount() {
        for (const feature of this.features)
            feature.mount();
    }
    // The store folds each event into a fresh immutable state; the registry hands
    // it here so feature controllers always render from the current snapshot.
    update(event, state) {
        if (this.disposed)
            return;
        if (state)
            this.state = state;
        for (const feature of this.features)
            feature.update(event, this.state);
    }
    dispose() {
        if (this.disposed)
            return;
        this.disposed = true;
        for (const feature of this.features)
            feature.dispose();
    }
}
