// ModeTransitionPromptController.ts — ACP state/action adapter for the
// Composer-owned React mode-transition prompt.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';
import type { ComposerModeTransitionPromptVM } from '../composer/ModeTransitionPrompt.js';
import { decisionOptionClass } from './decisionPresentation.js';

export interface ModeTransitionPromptHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
  setComposerPrompt(
    workspaceId: string,
    prompt: ComposerModeTransitionPromptVM | null
  ): void;
  focusComposerInput(workspaceId: string): void;
}

interface DecisionOption {
  optionId?: unknown;
  name?: unknown;
  kind?: unknown;
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function asOptionArray(value: unknown): DecisionOption[] {
  return Array.isArray(value) ? (value as DecisionOption[]) : [];
}

export class ModeTransitionPromptController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: ModeTransitionPromptHost;
  private assistantName = 'Agent';
  private activeRequestId = '';
  private readonly animationFrames = new Set<number>();

  constructor(workspaceId: string, host: ModeTransitionPromptHost) {
    this.workspaceId = workspaceId;
    this.host = host;
  }

  mount(): void {}

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    this.assistantName = state.identity.assistantName;
    const raw = event.raw;
    switch (event.type) {
      case 'permission_request': {
        if (raw.presentation !== 'mode_transition' || !raw.documentText) return;
        const storedState = asString(raw.decisionState) || 'pending';
        if (storedState !== 'pending') return;
        this.showPrompt(raw);
        return;
      }
      case 'permission_resolved':
      case 'permission_cancelled':
      case 'elicitation_cancelled':
        this.clearPrompt(asString(raw.requestId), true);
        return;
      case 'run_failed':
      case 'run_finished':
        this.clearPrompt(this.activeRequestId, true);
        return;
      default:
        return;
    }
  }

  dispose(): void {
    for (const frame of this.animationFrames) cancelAnimationFrame(frame);
    this.animationFrames.clear();
    this.activeRequestId = '';
    this.host.setComposerPrompt(this.workspaceId, null);
  }

  private showPrompt(event: RawHostMessage): void {
    const requestId = asString(event.requestId);
    const options = asOptionArray(event.options)
      .map((option) => {
        const optionId = asString(option.optionId);
        return {
          optionId,
          name: asString(option.name) || optionId || 'Select',
          kind: asString(option.kind),
          semanticClass: decisionOptionClass(option)
        };
      })
      .filter((option) => !!option.optionId);
    const interactive = !!requestId && options.length > 0;
    const input = this.host
      .getPanel(this.workspaceId)
      ?.querySelector<HTMLElement>('[data-role="input"]');
    const composerHadFocus = !!input?.contains(document.activeElement);

    this.activeRequestId = requestId;
    this.host.setComposerPrompt(this.workspaceId, {
      requestId,
      title: asString(event.title) || 'Mode transition',
      assistantName: this.assistantName,
      autoFocus: composerHadFocus,
      interactive,
      errorText: requestId
        ? 'The ACP Agent did not provide any response options.'
        : 'The ACP Agent did not provide a valid request identifier.',
      options,
      onRespond: (optionId) => {
        if (this.activeRequestId !== requestId || !optionId) return;
        this.bridge()?.sendAgentPermissionResponse(requestId, optionId);
      },
      onStop: () => this.bridge()?.sendAgentCommand('stop')
    });
  }

  private clearPrompt(requestId: string, restoreFocus: boolean): void {
    if (requestId && this.activeRequestId && this.activeRequestId !== requestId) return;
    const prompt = this.host
      .getPanel(this.workspaceId)
      ?.querySelector<HTMLElement>('[data-role="mode-transition-prompt"]');
    const promptHadFocus = !!prompt?.contains(document.activeElement);

    this.activeRequestId = '';
    this.host.setComposerPrompt(this.workspaceId, null);
    if (restoreFocus && promptHadFocus) {
      this.requestFrame(() => this.host.focusComposerInput(this.workspaceId));
    }
  }

  private requestFrame(callback: () => void): void {
    let frame = 0;
    frame = requestAnimationFrame(() => {
      this.animationFrames.delete(frame);
      callback();
    });
    this.animationFrames.add(frame);
  }

  private bridge(): AgentBridgePort | null {
    return this.host.bridgeFor(this.workspaceId);
  }
}
