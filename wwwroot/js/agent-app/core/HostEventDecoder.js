// HostEventDecoder.ts — validates and classifies raw host messages.
//
// Dispatch rule (plan Phase 2): lifecycle events must create/destroy the
// Controller before any content event can be handled. Only 'agent-workspace'
// events require a workspaceId AND an existing controller; 'app' and
// 'agent-global' events are never dropped for lacking a workspaceId. Invalid
// or unroutable messages are reported to diagnostics and ignored (no state
// change), never thrown.
import { scopeOfType } from '../contracts/host-events.js';
export class HostEventDecoder {
    hasController;
    diagnostics;
    constructor(hasController, diagnostics) {
        this.hasController = hasController;
        this.diagnostics = diagnostics;
    }
    decode(message) {
        const type = typeof message?.type === 'string' ? message.type : '';
        if (!type) {
            this.ignore('missing type', message);
            return null;
        }
        const scope = scopeOfType(type);
        if (!scope) {
            // Not an Agent-owned event (terminal/paste/etc. are handled elsewhere).
            return null;
        }
        const workspaceId = typeof message.workspaceId === 'string' ? message.workspaceId : '';
        switch (scope) {
            case 'app':
                return {
                    scope,
                    type: type,
                    settings: this.readSettings(message),
                    raw: message
                };
            case 'lifecycle':
                if (!workspaceId) {
                    this.ignore(`lifecycle event ${type} without workspaceId`, message);
                    return null;
                }
                return {
                    scope,
                    type: type,
                    workspaceId,
                    kind: message.kind === 'agent' ? 'agent' : message.kind === 'terminal' ? 'terminal' : undefined,
                    raw: message
                };
            case 'agent-global':
                return {
                    scope,
                    type: type,
                    workspaceId: workspaceId || undefined,
                    providers: Array.isArray(message.providers) ? message.providers : undefined,
                    text: typeof message.text === 'string' ? message.text : undefined,
                    raw: message
                };
            case 'agent-workspace':
                if (!workspaceId) {
                    this.ignore(`workspace event ${type} without workspaceId`, message);
                    return null;
                }
                if (!this.hasController(workspaceId)) {
                    this.ignore(`workspace event ${type} for unknown workspace ${workspaceId}`, message);
                    return null;
                }
                return {
                    scope,
                    type: type,
                    workspaceId,
                    raw: message
                };
        }
    }
    readSettings(message) {
        const settings = message.settings;
        return settings && typeof settings === 'object' ? settings : undefined;
    }
    ignore(reason, message) {
        this.diagnostics?.onIgnored(reason, message);
    }
}
