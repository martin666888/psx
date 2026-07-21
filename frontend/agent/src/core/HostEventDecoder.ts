// HostEventDecoder.ts — validates and classifies raw host messages.
//
// Dispatch rule (plan Phase 2): lifecycle events must create/destroy the
// Controller before any content event can be handled. Only 'agent-workspace'
// events require a workspaceId AND an existing controller; 'app' and
// 'agent-global' events are never dropped for lacking a workspaceId. Invalid
// or unroutable messages are reported to diagnostics and ignored (no state
// change), never thrown.

import type {
  AgentGlobalEventType,
  AgentHostEvent,
  AgentWorkspaceEventType,
  AppHostEventType,
  RawHostMessage,
  WorkspaceLifecycleEventType
} from '../contracts/host-events.js';
import { scopeOfType } from '../contracts/host-events.js';

export interface DecoderDiagnostics {
  onIgnored(reason: string, message: RawHostMessage): void;
}

export class HostEventDecoder {
  private readonly hasController: (workspaceId: string) => boolean;
  private readonly diagnostics: DecoderDiagnostics | undefined;

  constructor(
    hasController: (workspaceId: string) => boolean,
    diagnostics?: DecoderDiagnostics
  ) {
    this.hasController = hasController;
    this.diagnostics = diagnostics;
  }

  decode(message: RawHostMessage): AgentHostEvent | null {
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
          type: type as AppHostEventType,
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
          type: type as WorkspaceLifecycleEventType,
          workspaceId,
          kind: message.kind === 'agent' ? 'agent' : message.kind === 'terminal' ? 'terminal' : undefined,
          raw: message
        };

      case 'agent-global':
        return {
          scope,
          type: type as AgentGlobalEventType,
          workspaceId: workspaceId || undefined,
          providers: Array.isArray(message.providers) ? (message.providers as unknown[]) : undefined,
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
          type: type as AgentWorkspaceEventType,
          workspaceId,
          raw: message
        };
    }
  }

  private readSettings(message: RawHostMessage): Record<string, unknown> | undefined {
    const settings = message.settings;
    return settings && typeof settings === 'object' ? (settings as Record<string, unknown>) : undefined;
  }

  private ignore(reason: string, message: RawHostMessage): void {
    this.diagnostics?.onIgnored(reason, message);
  }
}
