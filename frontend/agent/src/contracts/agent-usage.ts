// agent-usage.ts — contracts for the global Usage panel and user profile.
//
// Wire shapes mirror the C# AgentUsageResult / AgentProfile payloads
// (camelCase). Provider identity is descriptor-driven and opaque to the UI;
// model/thread/parser diagnostics never cross the bridge.

export interface AgentUserProfile {
  displayName: string;
  avatarDataUrl: string | null;
  revision: number;
}

export interface UsageWindow {
  totalTokens: number;
}

export type UsageGapReason =
  | 'unsupported_source'
  | 'unsupported_format'
  | 'missing_session_logs'
  | 'ambiguous_session_logs'
  | 'unreadable_logs'
  | 'unmatched_sessions'
  | 'missing_session_id'
  | 'damaged_thread_files'
  | 'unregistered_provider';

export interface UsageCompleteness {
  status: 'available' | 'partial' | 'unavailable';
  reasons: UsageGapReason[];
  expectedSessions: number | null;
  matchedSessions: number | null;
  skippedFiles: number;
  badLines: number;
  untrackedThreads: number;
}

export interface ProviderUsageReport {
  providerKey: string;
  displayName: string;
  iconKey: string;
  dailyTokens: number[];
  today: UsageWindow;
  last7Days: UsageWindow;
  last30Days: UsageWindow;
  completeness: UsageCompleteness;
}

export interface UsageReport {
  heatmapStartDate: string;
  dailyTokens: number[];
  today: UsageWindow;
  last7Days: UsageWindow;
  last30Days: UsageWindow;
  providers: ProviderUsageReport[];
}

export type UsageWindowKey = 'today' | 'last7Days' | 'last30Days';

export type ConfigProviderState = 'available' | 'partial' | 'unavailable';

export interface ConfigFact {
  label: string;
  value: string;
}

export interface ConfigModelEntry {
  id: string;
  name: string | null;
  baseUrl: string | null;
}

export interface ConfigMcpServer {
  name: string;
  transport: string;
  target: string;
  enabled: boolean;
  envKeys: string[];
  headerKeys: string[];
}

export interface ConfigSkill {
  name: string;
}

export interface ProviderConfigReport {
  providerKey: string;
  displayName: string;
  iconKey: string;
  state: ConfigProviderState;
  facts: ConfigFact[];
  models: ConfigModelEntry[];
  mcpServers: ConfigMcpServer[];
  skills: ConfigSkill[];
  notes: string[];
}

export interface ConfigReport {
  providers: ProviderConfigReport[];
}

export type UsagePanelTab = 'usage' | 'config';

export interface UsageState {
  profile: AgentUserProfile;
  report: UsageReport | null;
  completeness: UsageCompleteness | null;
  generatedAt: string;
  timezone: string;
  status: 'idle' | 'loading' | 'error';
  errorText: string;
  panelOpen: boolean;
  activeTab: UsagePanelTab;
  configReport: ConfigReport | null;
  configGeneratedAt: string;
  configStatus: 'idle' | 'loading' | 'error';
  configErrorText: string;
  /** True after the first successful or failed config load in this process. */
  configLoadedOnce: boolean;
}

export type UsageListener = (state: UsageState) => void;

export function createInitialUsageState(): UsageState {
  return {
    profile: { displayName: '', avatarDataUrl: null, revision: -1 },
    report: null,
    completeness: null,
    generatedAt: '',
    timezone: '',
    status: 'idle',
    errorText: '',
    panelOpen: false,
    activeTab: 'usage',
    configReport: null,
    configGeneratedAt: '',
    configStatus: 'idle',
    configErrorText: '',
    configLoadedOnce: false
  };
}
