// reducer.ts — pure state folding for the identity/session/runtime slices.
//
// Phase 3 authority: every host message is validated by HostEventDecoder first,
// then folded here into a new AgentWorkspaceState. The reducer never touches the
// DOM — it runs in parallel with the legacy engine while tests assert the two
// stay equivalent. Timeline/composer/inspector/decisions slices are grown in
// Phase 4; here those events pass through unchanged.
//
// Faithful port of the legacy state transitions:
//   _updateAgentIdentity + _updateState (wwwroot/js/agent/thread.js)
//   _updateRuntimeStatus (wwwroot/js/agent/runtime.js)
//   _markSessionReady / _setContextUsed (wwwroot/js/agent/thread.js)
//
// Provider identity merge priority (plan Phase 3): agent_state overrides the
// agent_workspace_created seed, which overrides the neutral 'Agent' fallback.
// agent_providers only supplements the global catalog and is NOT required for
// identity resolution.

import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type {
  AgentWorkspaceState,
  ComposerCommand,
  ComposerConfigOption,
  ComposerMode,
  WorkspacePlanState,
  WorkspaceIdentity
} from '../contracts/workspace-state.js';
import { isRawPlanPayload, resolvePlanEntries } from './plan.js';

const RUNTIME_STATES: ReadonlySet<string> = new Set([
  'missing',
  'installing',
  'ready',
  'failed',
  'cancelled'
]);

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function nonEmptyTrimmed(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

/** Mirrors legacy _setContextUsed normalization. */
function normalizeContextUsed(value: unknown): number | null {
  if (value === null || value === undefined || value === '') return null;
  const numeric = Number(value);
  return Number.isFinite(numeric) && numeric >= 0 ? numeric : null;
}

/**
 * Applies the legacy _updateAgentIdentity merge: only non-empty trimmed fields
 * override the current identity. Used both for the creation seed and for
 * agent_state/runtime_status events, so later events (agent_state) naturally
 * win over the earlier creation seed.
 */
function mergeIdentity(identity: WorkspaceIdentity, source: RawHostMessage): WorkspaceIdentity {
  const providerKey = nonEmptyTrimmed(source.providerKey);
  const agentName = nonEmptyTrimmed(source.agentName);
  const assistantName = nonEmptyTrimmed(source.assistantName);
  if (!providerKey && !agentName && !assistantName) return identity;
  return {
    ...identity,
    providerKey: providerKey ?? identity.providerKey,
    agentName: agentName ?? identity.agentName,
    assistantName: assistantName ?? identity.assistantName
  };
}

/**
 * Seeds identity from an agent_workspace_created payload. Second in priority:
 * only fills fields the neutral default left blank-worthy; agent_state later
 * overrides via the same merge rule.
 */
export function seedIdentityFromCreation(
  state: AgentWorkspaceState,
  createdRaw: RawHostMessage | undefined
): AgentWorkspaceState {
  if (!createdRaw) return state;
  const identity = mergeIdentity(state.identity, createdRaw);
  if (identity === state.identity) return state;
  return { ...state, identity };
}

const EMPTY_PLAN: WorkspacePlanState = { active: false, runId: '', entries: [], fallbackText: '' };

/**
 * Mirrors legacy _upsertPlan: resolve the effective entries, and if they are
 * empty while the text is a raw plan JSON payload, skip the update entirely
 * (return the same slice) so an empty raw payload never wipes an active plan.
 */
function upsertPlan(plan: WorkspacePlanState, runId: unknown, rawEntries: unknown, text: unknown): WorkspacePlanState {
  const entries = resolvePlanEntries(rawEntries, text);
  if (entries.length === 0 && isRawPlanPayload(text)) return plan;
  return {
    active: true,
    runId: asString(runId) || plan.runId || 'plan',
    entries,
    fallbackText: asString(text)
  };
}

/** Mirrors legacy _configOptionRank: mode < model < effort < everything else. */
function configOptionRank(id: string): number {
  const order = ['mode', 'model', 'effort'];
  const index = order.indexOf(id);
  return index === -1 ? order.length : index;
}

/** Mirrors legacy _setAgentCommands normalization. */
function normalizeAgentCommands(value: unknown, assistantName: string): ComposerCommand[] {
  const list = Array.isArray(value) ? value : [];
  const source = assistantName + ' Agent';
  return list
    .map((command) => {
      if (typeof command === 'string') {
        return { name: command, description: 'Send to ' + source } as Record<string, unknown>;
      }
      return (command ?? {}) as Record<string, unknown>;
    })
    .filter((command) => typeof command.name === 'string' && (command.name as string).trim())
    .map((command) => {
      const trimmed = (command.name as string).trim();
      const name = trimmed.startsWith('/') ? trimmed : '/' + trimmed;
      const description = command.description;
      return {
        source,
        name,
        label: typeof description === 'string' && description ? description : 'Send to ' + source,
        agent: true
      };
    });
}

/** Mirrors legacy _setModes filtering (keeps entries with an id). */
function normalizeModes(value: unknown): ComposerMode[] {
  const list = Array.isArray(value) ? value : [];
  return list
    .map((mode) => (mode ?? {}) as Record<string, unknown>)
    .filter((mode) => typeof mode.id === 'string' && mode.id)
    .map((mode) => ({
      id: mode.id as string,
      name: asString(mode.name),
      description: asString(mode.description)
    }));
}

/** Mirrors legacy _setConfigOptions filter + rank sort. */
function normalizeConfigOptions(value: unknown): ComposerConfigOption[] {
  const list = Array.isArray(value) ? value : [];
  return list
    .map((option) => (option ?? {}) as Record<string, unknown>)
    .filter((option) =>
      typeof option.id === 'string' &&
      option.id &&
      option.type === 'select' &&
      Array.isArray(option.options) &&
      option.options.length > 0)
    .map((option) => ({
      id: option.id as string,
      name: asString(option.name),
      description: asString(option.description),
      currentValue: asString(option.currentValue),
      options: (option.options as unknown[]).map((item) => {
        const it = (item ?? {}) as Record<string, unknown>;
        return { value: asString(it.value), name: asString(it.name), description: asString(it.description) };
      })
    }))
    .sort((a, b) => configOptionRank(a.id) - configOptionRank(b.id));
}

/** Folds one validated workspace event into a new state (pure, no DOM). */
export function reduceWorkspaceState(
  state: AgentWorkspaceState,
  event: AgentWorkspaceEvent
): AgentWorkspaceState {
  const raw = event.raw;

  switch (event.type) {
    case 'agent_state': {
      const status = asString(raw.status) || 'ready';
      const identity: WorkspaceIdentity = {
        ...mergeIdentity(state.identity, raw),
        supportsImage: raw.supportsImage !== false
      };
      const contextUsedTokens =
        'contextUsedTokens' in raw
          ? normalizeContextUsed(raw.contextUsedTokens)
          : state.session.contextUsedTokens;
      return {
        ...state,
        identity,
        session: {
          ...state.session,
          status,
          cwd: asString(raw.cwd),
          sessionId: asString(raw.sessionId),
          currentThreadId: raw.threadId ? asString(raw.threadId) : state.session.currentThreadId,
          busy: !!raw.busy,
          isDraft: raw.isDraft === true,
          isRestoring: status === 'restoring',
          isTranscriptOnly: status === 'transcript_only',
          contextUsedTokens
        }
      };
    }

    case 'runtime_status': {
      const rawState = asString(raw.state);
      const runtimeState = RUNTIME_STATES.has(rawState) ? rawState : 'missing';
      return {
        ...state,
        identity: mergeIdentity(state.identity, raw),
        runtime: {
          state: runtimeState,
          message: asString(raw.message) || 'Agent runtime is not installed.',
          canInstall: !!raw.canInstall,
          canCancel: !!raw.canCancel
        }
      };
    }

    case 'agent_ready': {
      const sessionId = asString(raw.sessionId);
      if (!sessionId || state.session.readySessionId === sessionId) return state;
      return { ...state, session: { ...state.session, readySessionId: sessionId } };
    }

    case 'agent_usage_update': {
      return {
        ...state,
        session: { ...state.session, contextUsedTokens: normalizeContextUsed(raw.contextUsedTokens) }
      };
    }

    case 'agent_thread_loaded': {
      const contextUsedTokens =
        'contextUsedTokens' in raw
          ? normalizeContextUsed(raw.contextUsedTokens)
          : state.session.contextUsedTokens;
      // Inspector plan slice: legacy _loadThread resets the plan when clearing,
      // then upserts every plan-role message in order (the last one wins).
      let plan = raw.clear ? EMPTY_PLAN : state.inspector.plan;
      const messages = Array.isArray(raw.messages) ? raw.messages : [];
      for (const message of messages) {
        const msg = (message ?? {}) as Record<string, unknown>;
        if (msg.role === 'plan') {
          plan = upsertPlan(plan, msg.runId, msg.planEntries, msg.text);
        }
      }
      return {
        ...state,
        session: {
          ...state.session,
          currentThreadId: asString(raw.threadId),
          cwd: asString(raw.cwd) || state.session.cwd,
          sessionId: asString(raw.sessionId) || state.session.sessionId,
          contextUsedTokens
        },
        inspector: plan === state.inspector.plan ? state.inspector : { ...state.inspector, plan }
      };
    }

    case 'plan_update': {
      const plan = upsertPlan(state.inspector.plan, raw.runId, raw.entries, raw.text);
      if (plan === state.inspector.plan) return state;
      return { ...state, inspector: { ...state.inspector, plan } };
    }

    case 'agent_cleared': {
      if (!state.inspector.plan.active) return state;
      return { ...state, inspector: { ...state.inspector, plan: EMPTY_PLAN } };
    }

    case 'agent_commands': {
      return {
        ...state,
        composer: {
          ...state.composer,
          agentCommands: normalizeAgentCommands(raw.commands, state.identity.assistantName),
          agentCommandsReady: raw.ready === true
        }
      };
    }

    case 'agent_modes': {
      const modes = normalizeModes(raw.modes);
      const currentModeId = asString(raw.currentModeId) || state.composer.currentModeId;
      return { ...state, composer: { ...state.composer, modes, currentModeId } };
    }

    case 'agent_config_options': {
      const configOptions = normalizeConfigOptions(raw.options);
      const modeOption = configOptions.find((option) => option.id === 'mode');
      const currentModeId = modeOption?.currentValue || state.composer.currentModeId;
      return { ...state, composer: { ...state.composer, configOptions, currentModeId } };
    }

    case 'agent_mode_current': {
      const currentModeId = asString(raw.currentModeId);
      if (currentModeId === state.composer.currentModeId) return state;
      return { ...state, composer: { ...state.composer, currentModeId } };
    }

    default:
      // Timeline / composer / inspector / decision events do not affect the
      // identity/session/runtime slices in Phase 3. They flow to the legacy
      // engine unchanged and reduce into their own slices in Phase 4.
      return state;
  }
}
