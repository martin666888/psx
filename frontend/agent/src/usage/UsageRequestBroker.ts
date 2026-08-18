// UsageRequestBroker.ts — the single owner of profile + usage + config bridge
// commands.
//
// Panel data is process-global, so every command travels through the root
// bridge and remains available before the first Agent workspace exists and
// after the last one closes. Every request carries a requestId and only the
// matching reply resolves it:
//   - profile_get / profile_set_* replies echo the requestId; profile
//     broadcasts (no requestId) still apply through the revision guard.
//   - usage_report / config_report replies must match the in-flight requestId
//     or are dropped (late scans, superseded refreshes).
// One 30s timeout per attempt plus one retry on the root bridge, then an inline
// error. Config is distinct from live ACP agent_config_options.

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
  UsageWindow
} from '../contracts/agent-usage.js';
import { UsageStore } from './UsageStore.js';

export interface UsageRequestHost {
  sendGlobalCommand(command: string, value?: string | boolean, requestId?: string): void;
}

export interface UsageRequestBrokerOptions {
  timeoutMs?: number;
}

const DEFAULT_TIMEOUT_MS = 30000;
const USAGE_HEATMAP_DAYS = 365;
const USAGE_GAP_REASONS = new Set<UsageGapReason>([
  'unsupported_source',
  'unsupported_format',
  'missing_session_logs',
  'ambiguous_session_logs',
  'unreadable_logs',
  'unmatched_sessions',
  'missing_session_id',
  'damaged_thread_files',
  'unregistered_provider'
]);
const TIMEOUT_TEXT = 'Loading usage timed out.';
const DEFAULT_ERROR_TEXT = 'Unable to load usage.';
const TIMEOUT_CONFIG_TEXT = 'Loading config timed out.';
const DEFAULT_CONFIG_ERROR_TEXT = 'Unable to load config.';

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
  return {
    providerKey: str(provider.providerKey),
    displayName: str(provider.displayName),
    iconKey: str(provider.iconKey) || 'agent',
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
  const label = str(fact.label);
  if (!label) return null;
  return { label, value: str(fact.value) };
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
  private readonly host: UsageRequestHost;

  private counter = 0;
  private readonly pendingProfileRequests = new Set<string>();

  private usageRequestId = '';
  private usageValue: 'cached' | 'force' = 'cached';
  private usageRetried = false;
  private usageTimer: ReturnType<typeof setTimeout> | null = null;

  private configRequestId = '';
  private configValue: 'cached' | 'force' = 'cached';
  private configRetried = false;
  private configTimer: ReturnType<typeof setTimeout> | null = null;

  private disposed = false;

  constructor(host: UsageRequestHost, store: UsageStore, options?: UsageRequestBrokerOptions) {
    this.host = host;
    this.store = store;
    this.timeoutMs = options?.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    // The History footer exists during terminal-only startup, so its profile
    // bootstrap cannot wait for an Agent workspace lifecycle event.
    this.requestProfile();
  }

  // --- Request entry points ---------------------------------------------------

  requestProfile(): void {
    if (this.disposed) return;
    const requestId = this.nextId('p');
    this.pendingProfileRequests.add(requestId);
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

  // --- Response handlers ------------------------------------------------------

  handleProfile(raw: RawHostMessage): void {
    if (this.disposed) return;
    const requestId = str(raw.requestId);
    if (requestId) this.pendingProfileRequests.delete(requestId);
    // A server-side error leaves the stored revision unchanged; do not apply.
    if (strOrNull(raw.error)) return;
    this.store.applyProfile(normalizeProfile(raw));
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
      this.store.applyUsageError(error || DEFAULT_ERROR_TEXT);
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
      this.store.applyConfigError(error || DEFAULT_CONFIG_ERROR_TEXT);
      return;
    }
    this.store.applyConfigReport(normalizeConfigReport(raw.report), str(raw.generatedAt));
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
    this.pendingProfileRequests.add(requestId);
    this.host.sendGlobalCommand(command, value, requestId);
  }

  private sendUsage(): void {
    const requestId = this.nextId('u');
    this.usageRequestId = requestId;
    this.host.sendGlobalCommand('usage_report', this.usageValue, requestId);
    this.usageTimer = setTimeout(() => this.onUsageTimeout(requestId), this.timeoutMs);
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
    this.store.applyUsageError(TIMEOUT_TEXT);
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
    this.store.applyConfigError(TIMEOUT_CONFIG_TEXT);
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
