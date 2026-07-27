// AgentWorkspaceController.ts — owns a single Agent workspace.
//
// Holds the workspace state slice and coordinates the lifecycle of its domain
// feature controllers.
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
