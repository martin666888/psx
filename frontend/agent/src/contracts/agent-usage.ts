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

export interface UsageState {
  profile: AgentUserProfile;
  report: UsageReport | null;
  completeness: UsageCompleteness | null;
  generatedAt: string;
  timezone: string;
  status: 'idle' | 'loading' | 'error';
  errorText: string;
  panelOpen: boolean;
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
    panelOpen: false
  };
}
