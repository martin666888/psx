// agent-usage.ts — contracts for the global Usage panel and user profile.
//
// Wire shapes mirror the C# AgentUsageResult / AgentProfile payloads
// (camelCase). The UsageStore holds the profile plus the last usage report;
// the UsageRequestBroker fills them through requestId-matched replies. No
// consumer inspects providerKey — sections render purely by which fields exist.

export interface AgentUserProfile {
  displayName: string;
  avatarDataUrl: string | null;
  revision: number;
}

export interface UsageTokens {
  input: number;
  output: number;
  cacheRead: number;
  cacheCreation: number;
}

export interface UsageModelRow extends UsageTokens {
  model: string;
}

export interface UsageContextSnapshot {
  threadTitle: string;
  usedTokens: number | null;
  windowTokens: number | null;
}

export interface UsageProviderSection {
  providerKey: string;
  iconKey: string;
  exactUsage: { modelRows: UsageModelRow[] } | null;
  contextSnapshots: UsageContextSnapshot[] | null;
  sourceKey: string;
}

export interface UsageWindow {
  activeThreads: number;
  turns: number;
  tokens: UsageTokens;
  cacheHitRate: number | null;
  providerSections: UsageProviderSection[];
}

export interface UsageReport {
  heatmap: number[];
  heatmapStartDate: string;
  today: UsageWindow;
  last7Days: UsageWindow;
  last30Days: UsageWindow;
}

export interface UsageSourceStatus {
  key: string;
  status: 'available' | 'partial' | 'unavailable';
  scannedFiles: number;
  skippedFiles: number;
  badLines: number;
  expectedSessions: number | null;
  matchedSessions: number | null;
  parserVersion: string;
  lastScanAt: string;
  detail: string | null;
}

export type UsageWindowKey = 'today' | 'last7Days' | 'last30Days';

export interface UsageState {
  profile: AgentUserProfile;
  report: UsageReport | null;
  sources: UsageSourceStatus[];
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
    sources: [],
    generatedAt: '',
    timezone: '',
    status: 'idle',
    errorText: '',
    panelOpen: false
  };
}
