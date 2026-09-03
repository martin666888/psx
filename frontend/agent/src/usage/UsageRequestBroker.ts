// UsageRequestBroker.ts — the single owner of profile + usage + config bridge
// commands.
//
// Panel data is process-global, so every command travels through the root
// bridge and remains available before the first Agent workspace exists and
// after the last one closes. Every request carries a requestId and only the
// matching reply resolves it:
//   - profile_get / profile_set_* replies echo the requestId; profile
//     broadcasts (no requestId) still apply through the revision guard.
//     Matching profile_set_* errors write profileError for the settings page;
//     unmatched or broadcast errors are dropped and never apply the payload.
//   - usage_report / config_report replies must match the in-flight requestId
//     or are dropped (late scans, superseded refreshes).
// One timeout per attempt plus one retry on the root bridge, then an inline
// error: usage_report gets 120s (local-all scans can be slow), config and the
// other requests keep 30s. Config is distinct from live ACP agent_config_options.

import type { RawHostMessage } from '../contracts/host-events.js';
import type {
  AgentUserProfile,
  ConfigFact,
  ConfigMcpServer,
  ConfigModelEntry,
  ConfigReport,
  ConfigSkill,
  ProviderConfigReport,
  ProviderUsageReport,
  UsageCompleteness,
  UsageGapReason,
  UsageReport,
  UsageScope,
  UsageWindow
} from '../contracts/agent-usage.js';
import { UsageStore } from './UsageStore.js';
import { safeDshRegistry, type DshRegistryKey } from './settingsRegistry.js';

export interface UsageRequestHost {
  sendGlobalCommand(command: string, value?: string | boolean, requestId?: string): void;
  sendAppSettingsCommand(
    action: 'get' | 'set_dsh_registry' | 'set_locale',
    requestId: string,
    registry?: 'official' | 'npmmirror',
    localeMode?: 'system' | 'zh-Hans' | 'zh-Hant' | 'en' | 'ja'
  ): void;
}

export interface UsageRequestBrokerOptions {
  timeoutMs?: number;
  usageTimeoutMs?: number;
}

const DEFAULT_TIMEOUT_MS = 30000;
const USAGE_TIMEOUT_MS = 120000;
const USAGE_HEATMAP_DAYS = 365;
const USAGE_GAP_REASONS = new Set<UsageGapReason>([
  'unsupported_source',
  'unsupported_format',
  'missing_session_logs',
  'ambiguous_session_logs',
  'unreadable_logs',
  'discovery_truncated',
  'unmatched_sessions',
  'missing_session_id',
  'damaged_thread_files',
  'unregistered_provider',
  'lineage_unresolved',
  'extractor_unavailable'
]);
/** Fixed settings-locale keys — the sentence is resolved at render so a
 *  language switch re-localizes any visible error. Backend error strings are
 *  technical detail and never reach the DOM. */
const USAGE_TIMEOUT_KEY = 'usage.timeout';
const USAGE_LOAD_FAILED_KEY = 'usage.loadFailed';
const CONFIG_TIMEOUT_KEY = 'config.timeout';
const CONFIG_LOAD_FAILED_KEY = 'config.loadFailed';
const PROFILE_SAVE_FAILED_KEY = 'profile.saveFailed';
const REGISTRY_SAVE_FAILED_KEY = 'registry.saveFailed';
const LANGUAGE_SAVE_FAILED_KEY = 'language.saveFailed';

function num(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

function nonNegativeNum(value: unknown): number {
  return Math.max(0, num(value));
}

function numOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function str(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function strOrNull(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {};
}

export function normalizeProfile(raw: RawHostMessage): AgentUserProfile {
  return {
    displayName: str(raw.displayName),
    avatarDataUrl: strOrNull(raw.avatarDataUrl),
    revision: num(raw.revision)
  };
}

function normalizeWindow(value: unknown): UsageWindow {
  const w = asRecord(value);
  return {
    totalTokens: nonNegativeNum(w.totalTokens)
  };
}

function normalizeDailyTokens(value: unknown): number[] {
  const raw = Array.isArray(value) ? value : [];
  return Array.from(
    { length: USAGE_HEATMAP_DAYS },
    (_, index) => nonNegativeNum(raw[index])
  );
}

function normalizeProvider(value: unknown): ProviderUsageReport {
  const provider = asRecord(value);
  // Older backends omit scope; PSX-owned session reports are the legacy default.
  const scope: UsageScope = str(provider.scope) === 'local_all' ? 'local_all' : 'psx_sessions';
  return {
    providerKey: str(provider.providerKey),
    displayName: str(provider.displayName),
    iconKey: str(provider.iconKey) || 'agent',
    scope,
    dailyTokens: normalizeDailyTokens(provider.dailyTokens),
    today: normalizeWindow(provider.today),
    last7Days: normalizeWindow(provider.last7Days),
    last30Days: normalizeWindow(provider.last30Days),
    completeness: normalizeCompleteness(provider.completeness)
  };
}

export function normalizeUsageReport(value: unknown): UsageReport {
  const r = asRecord(value);
  return {
    heatmapStartDate: str(r.heatmapStartDate),
    dailyTokens: normalizeDailyTokens(r.dailyTokens),
    today: normalizeWindow(r.today),
    last7Days: normalizeWindow(r.last7Days),
    last30Days: normalizeWindow(r.last30Days),
    providers: Array.isArray(r.providers)
      ? r.providers.map(normalizeProvider).filter(provider => provider.providerKey)
      : []
  };
}

export function normalizeCompleteness(value: unknown): UsageCompleteness {
  const c = asRecord(value);
  const status = str(c.status);
  const reasons = Array.isArray(c.reasons)
    ? c.reasons
        .map(str)
        .filter((reason): reason is UsageGapReason =>
          USAGE_GAP_REASONS.has(reason as UsageGapReason))
    : [];
  return {
    status: status === 'partial' || status === 'unavailable' ? status : 'available',
    reasons: [...new Set(reasons)],
    expectedSessions: numOrNull(c.expectedSessions),
    matchedSessions: numOrNull(c.matchedSessions),
    skippedFiles: nonNegativeNum(c.skippedFiles),
    badLines: nonNegativeNum(c.badLines),
    untrackedThreads: nonNegativeNum(c.untrackedThreads)
  };
}

function normalizeStringList(value: unknown): string[] {
  return Array.isArray(value) ? value.map(str).filter(Boolean) : [];
}

function normalizeConfigFact(value: unknown): ConfigFact | null {
  const fact = asRecord(value);
  const labelKey = str(fact.labelKey);
  if (!labelKey) return null;
  return { labelKey, value: str(fact.value) };
}

function normalizeConfigModel(value: unknown): ConfigModelEntry | null {
  const model = asRecord(value);
  const id = str(model.id);
  if (!id) return null;
  return {
    id,
    name: strOrNull(model.name),
    baseUrl: strOrNull(model.baseUrl)
  };
}

function normalizeConfigMcp(value: unknown): ConfigMcpServer | null {
  const mcp = asRecord(value);
  const name = str(mcp.name);
  if (!name) return null;
  return {
    name,
    transport: str(mcp.transport) || 'stdio',
    target: str(mcp.target),
    enabled: mcp.enabled !== false,
    envKeys: normalizeStringList(mcp.envKeys),
    headerKeys: normalizeStringList(mcp.headerKeys)
  };
}

function normalizeConfigSkill(value: unknown): ConfigSkill | null {
  const skill = asRecord(value);
  const name = str(skill.name);
  return name ? { name } : null;
}

function normalizeProviderConfig(value: unknown): ProviderConfigReport {
  const provider = asRecord(value);
  const stateRaw = str(provider.state);
  const state =
    stateRaw === 'partial' || stateRaw === 'unavailable' ? stateRaw : 'available';
  return {
    providerKey: str(provider.providerKey),
    displayName: str(provider.displayName),
    iconKey: str(provider.iconKey) || 'agent',
    state,
    facts: Array.isArray(provider.facts)
      ? provider.facts.map(normalizeConfigFact).filter((f): f is ConfigFact => !!f)
      : [],
    models: Array.isArray(provider.models)
      ? provider.models
          .map(normalizeConfigModel)
          .filter((m): m is ConfigModelEntry => !!m)
      : [],
    mcpServers: Array.isArray(provider.mcpServers)
      ? provider.mcpServers
          .map(normalizeConfigMcp)
          .filter((m): m is ConfigMcpServer => !!m)
      : [],
    skills: Array.isArray(provider.skills)
      ? provider.skills
          .map(normalizeConfigSkill)
          .filter((s): s is ConfigSkill => !!s)
      : [],
    notes: normalizeStringList(provider.notes)
  };
}

export function normalizeConfigReport(value: unknown): ConfigReport {
  const r = asRecord(value);
  return {
    providers: Array.isArray(r.providers)
      ? r.providers
          .map(normalizeProviderConfig)
          .filter(provider => provider.providerKey)
      : []
  };
}

export class UsageRequestBroker {
  private readonly store: UsageStore;
  private readonly timeoutMs: number;
  private readonly usageTimeoutMs: number;
  private readonly host: UsageRequestHost;

  private counter = 0;
  private readonly pendingProfileRequests = new Map<string, 'read' | 'mutation'>();
  private profileMutationId = '';

  private usageRequestId = '';
  private usageValue: 'cached' | 'force' = 'cached';
  private usageRetried = false;
  private usageTimer: ReturnType<typeof setTimeout> | null = null;

  private configRequestId = '';
  private configValue: 'cached' | 'force' = 'cached';
  private configRetried = false;
  private configTimer: ReturnType<typeof setTimeout> | null = null;

  private settingsRequestId = '';
  private pendingSettingsAction: '' | 'set_dsh_registry' | 'set_locale' = '';

  private disposed = false;

  constructor(host: UsageRequestHost, store: UsageStore, options?: UsageRequestBrokerOptions) {
    this.host = host;
    this.store = store;
    this.timeoutMs = options?.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    this.usageTimeoutMs = options?.usageTimeoutMs ?? USAGE_TIMEOUT_MS;
    // The Usage panel exists during terminal-only startup, so its profile
    // bootstrap cannot wait for an Agent workspace lifecycle event.
    this.requestProfile();
  }

  // --- Request entry points ---------------------------------------------------

  requestProfile(): void {
    if (this.disposed) return;
    const requestId = this.nextId('p');
    this.pendingProfileRequests.set(requestId, 'read');
    this.host.sendGlobalCommand('profile_get', undefined, requestId);
  }

  setDisplayName(name: string): void {
    this.sendProfileMutation('profile_set_name', name);
  }

  setAvatar(base64Png: string): void {
    this.sendProfileMutation('profile_set_avatar', base64Png);
  }

  /** Panel open uses 'cached'; the Refresh button uses 'force'. Each call
   * supersedes any in-flight scan (the old requestId's reply is then dropped). */
  requestUsage(force: boolean): void {
    if (this.disposed) return;
    this.usageValue = force ? 'force' : 'cached';
    this.usageRetried = false;
    this.store.applyUsageLoading();
    this.sendUsage();
  }

  /** Config tab: first activation uses cached; Refresh uses force. */
  requestConfig(force: boolean): void {
    if (this.disposed) return;
    this.configValue = force ? 'force' : 'cached';
    this.configRetried = false;
    this.store.applyConfigLoading();
    this.sendConfig();
  }

  requestSettings(): void {
    if (this.disposed) return;
    const requestId = this.nextId('s');
    this.settingsRequestId = requestId;
    this.host.sendAppSettingsCommand('get', requestId);
  }

  setDshRegistry(registry: DshRegistryKey): void {
    if (this.disposed) return;
    const requestId = this.nextId('s');
    this.settingsRequestId = requestId;
    this.pendingSettingsAction = 'set_dsh_registry';
    this.store.setSettingsDraft(registry);
    this.host.sendAppSettingsCommand('set_dsh_registry', requestId, registry);
  }

  /** Persist a UI locale mode; the broadcast snapshot applies it process-wide
   * (the shell switches i18next on resolvedLocale). */
  setLocale(mode: 'system' | 'zh-Hans' | 'zh-Hant' | 'en' | 'ja'): void {
    if (this.disposed) return;
    const requestId = this.nextId('s');
    this.settingsRequestId = requestId;
    this.pendingSettingsAction = 'set_locale';
    this.host.sendAppSettingsCommand('set_locale', requestId, undefined, mode);
  }

  // --- Response handlers ------------------------------------------------------

  handleProfile(raw: RawHostMessage): void {
    if (this.disposed) return;
    const requestId = str(raw.requestId);
    const requestKind = requestId ? this.pendingProfileRequests.get(requestId) : undefined;
    const matchedPending = requestKind !== undefined;
    // Profile broadcasts intentionally omit requestId. A reply carrying an id
    // must belong to this broker; late, duplicate and never-issued replies are
    // not allowed to resolve state.
    if (requestId && !matchedPending) return;
    if (requestId) this.pendingProfileRequests.delete(requestId);
    const matchedMutation = Boolean(requestId && requestId === this.profileMutationId);
    if (matchedMutation) this.profileMutationId = '';

    const error = strOrNull(raw.error);
    if (error) {
      // Only a matching in-flight request may surface an error; broadcasts and
      // late replies stay silent so a superseded save cannot clobber a newer
      // one. The backend error text stays internal — the panel shows the
      // fixed save-failed copy.
      if (matchedPending) {
        this.store.applyProfileError(PROFILE_SAVE_FAILED_KEY, {
          saving: Boolean(this.profileMutationId)
        });
      }
      return;
    }
    this.store.applyProfile(normalizeProfile(raw), {
      saving: Boolean(this.profileMutationId),
      clearError: requestKind === 'mutation'
    });
  }

  handleUsageReport(raw: RawHostMessage): void {
    if (this.disposed) return;
    const requestId = str(raw.requestId);
    // Only the in-flight request resolves; late/superseded scans are dropped.
    if (!this.usageRequestId || requestId !== this.usageRequestId) return;
    this.clearUsageTimer();
    this.usageRequestId = '';

    const error = strOrNull(raw.error);
    if (error || !raw.report || !raw.completeness) {
      // Fixed backend codes resolve through the settings locales at render;
      // anything unexpected falls back to the generic copy.
      this.store.applyUsageError(
        error === 'usage.scan_failed' ? 'usage.scanFailed' : USAGE_LOAD_FAILED_KEY);
      return;
    }
    this.store.applyUsageReport(
      normalizeUsageReport(raw.report),
      normalizeCompleteness(raw.completeness),
      str(raw.generatedAt),
      str(raw.timezone)
    );
  }

  handleConfigReport(raw: RawHostMessage): void {
    if (this.disposed) return;
    const requestId = str(raw.requestId);
    if (!this.configRequestId || requestId !== this.configRequestId) return;
    this.clearConfigTimer();
    this.configRequestId = '';

    const error = strOrNull(raw.error);
    if (error || !raw.report) {
      // `error` is either empty or a fixed note code (Models/AgentConfigModels.cs);
      // ConfigPanel resolves the locale key at render time.
      this.store.applyConfigError(error || CONFIG_LOAD_FAILED_KEY);
      return;
    }
    this.store.applyConfigReport(normalizeConfigReport(raw.report), str(raw.generatedAt));
  }

  handleAppSettings(raw: RawHostMessage): void {
    if (this.disposed) return;
    const requestId = str(raw.requestId);

    if (requestId) {
      // A reply must match the currently pending request exactly; late or
      // superseded replies are dropped whole and resolve nothing.
      if (!this.settingsRequestId || requestId !== this.settingsRequestId) return;

      const action = this.pendingSettingsAction;
      this.settingsRequestId = '';
      this.pendingSettingsAction = '';

      // Save failures surface the fixed key for the section that owns the
      // request — the backend's composed errorMessage never reaches the DOM,
      // and the panel resolves the sentence at render time.
      const failed = strOrNull(raw.errorClass) !== null;
      this.applySettingsSnapshot(raw, {
        fromReply: true,
        error: failed && action !== 'set_locale' ? REGISTRY_SAVE_FAILED_KEY : '',
        languageError: failed && action === 'set_locale' ? LANGUAGE_SAVE_FAILED_KEY : ''
      });
      return;
    }

    // Broadcast: refresh the snapshot only. A pending mutation stays armed
    // for its own reply; broadcasts never resolve or unbind it.
    this.applySettingsSnapshot(raw, { fromReply: false, error: '', languageError: '' });
  }

  private applySettingsSnapshot(
    raw: RawHostMessage,
    options: { fromReply: boolean; error: string; languageError: string }
  ): void {
    const revision = Number(raw.revision);
    if (!Number.isFinite(revision) || revision < 0) return;
    const state = this.store.getState();
    const failed = options.error !== '' || options.languageError !== '';
    // Broadcasts dedupe at the seen revision. Replies apply when they carry a
    // failure (a failed save does not bump the revision but must still
    // surface its error); success replies older than the seen revision are
    // stale and dropped.
    if (!options.fromReply && revision <= state.settingsRevision) return;
    if (options.fromReply && !failed && revision < state.settingsRevision) return;

    const registry = safeDshRegistry(raw.dshRegistry);
    const syncDraft = !options.fromReply || !failed;
    this.store.applyAppSettings({
      revision: Math.max(revision, state.settingsRevision),
      dshRegistry: registry,
      draft: syncDraft ? registry : undefined,
      error: options.error,
      languageError: options.languageError,
      localeMode: typeof raw.localeMode === 'string' ? raw.localeMode : undefined,
      resolvedLocale:
        typeof raw.resolvedLocale === 'string' ? raw.resolvedLocale : undefined
    });
  }

  dispose(): void {
    this.disposed = true;
    this.clearUsageTimer();
    this.clearConfigTimer();
  }

  // --- Internals --------------------------------------------------------------

  private sendProfileMutation(command: string, value: string): void {
    if (this.disposed) return;
    const requestId = this.nextId('p');
    this.pendingProfileRequests.set(requestId, 'mutation');
    this.profileMutationId = requestId;
    this.store.beginProfileSave();
    this.host.sendGlobalCommand(command, value, requestId);
  }

  private sendUsage(): void {
    const requestId = this.nextId('u');
    this.usageRequestId = requestId;
    this.host.sendGlobalCommand('usage_report', this.usageValue, requestId);
    this.usageTimer = setTimeout(() => this.onUsageTimeout(requestId), this.usageTimeoutMs);
  }

  private sendConfig(): void {
    const requestId = this.nextId('c');
    this.configRequestId = requestId;
    this.host.sendGlobalCommand('config_report', this.configValue, requestId);
    this.configTimer = setTimeout(() => this.onConfigTimeout(requestId), this.timeoutMs);
  }

  private onUsageTimeout(requestId: string): void {
    this.usageTimer = null;
    if (this.disposed || requestId !== this.usageRequestId) return;
    this.usageRequestId = '';
    if (!this.usageRetried) {
      this.usageRetried = true;
      this.sendUsage();
      return;
    }
    this.store.applyUsageError(USAGE_TIMEOUT_KEY);
  }

  private onConfigTimeout(requestId: string): void {
    this.configTimer = null;
    if (this.disposed || requestId !== this.configRequestId) return;
    this.configRequestId = '';
    if (!this.configRetried) {
      this.configRetried = true;
      this.sendConfig();
      return;
    }
    this.store.applyConfigError(CONFIG_TIMEOUT_KEY);
  }

  private clearUsageTimer(): void {
    if (this.usageTimer !== null) {
      clearTimeout(this.usageTimer);
      this.usageTimer = null;
    }
  }

  private clearConfigTimer(): void {
    if (this.configTimer !== null) {
      clearTimeout(this.configTimer);
      this.configTimer = null;
    }
  }

  private nextId(prefix: string): string {
    this.counter += 1;
    return prefix + this.counter;
  }
}
