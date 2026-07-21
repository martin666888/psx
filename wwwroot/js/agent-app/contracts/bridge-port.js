// bridge-port.ts — the workspace-scoped outbound bridge surface.
//
// Wraps the object returned by the global Bridge.createAgentScope(workspaceId).
// Feature controllers depend on this interface, never on the global Bridge, so
// they stay testable and scoped to a single workspace.
export {};
