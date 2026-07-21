// entry.ts — composition root for the Agent ESM app.
//
// main.js dynamically imports the compiled ./agent-app/entry.js AFTER it has
// already created the TerminalManager and sent `ready`. createAgentApp wires the
// native WorkspaceHost (panel shells + workspace-scoped bridges) to the registry
// and returns a single `handle(message)` sink. main.js drains its staging queue
// through this sink and forwards every later Agent host event to it.
import { WorkspaceHost } from './workspace/WorkspaceHost.js';
import { AgentWorkspaceRegistry } from './workspace/AgentWorkspaceRegistry.js';
export function createAgentApp(options) {
    const host = new WorkspaceHost(options.terminalManager, options.container, options.template);
    const registry = new AgentWorkspaceRegistry(host, {
        onIgnored(reason) {
            console.debug('[agent] ignored host event:', reason);
        }
    });
    return {
        handle(message) {
            registry.handle(message);
        }
    };
}
