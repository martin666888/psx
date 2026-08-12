// UsagePanel.tsx — the global Usage panel dialog (profile + usage report).
//
// One singleton Dialog for the whole process, opened from the History dock
// footer. The selected window exposes backend-owned aggregate and per-Provider
// totals. The fixed annual heatmap is built from exact daily token totals;
// model/thread/parser detail never crosses the public contract.

import { useEffect, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import { RefreshCwIcon } from 'lucide-react';
import { ProviderIcon } from '../components/ProviderIcon.js';
import { Button } from '../components/ui/button.js';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogTitle
} from '../components/ui/dialog.js';
import type {
  ProviderUsageReport,
  UsageCompleteness,
  UsageGapReason,
  UsagePanelTab,
  UsageState,
  UsageWindowKey
} from '../contracts/agent-usage.js';
import { ConfigPanel } from './ConfigPanel.js';

export interface UsagePanelProps {
  state: UsageState;
  onOpenChange(open: boolean): void;
  /** Usage Refresh button: bypasses the backend cache. */
  onRefresh(): void;
  /** Usage error retry: an ordinary cached load. */
  onRetry(): void;
  /** Switch between 用量 / 配置 tabs (host persists activeTab). */
  onSelectTab(tab: UsagePanelTab): void;
  /** Config Refresh / first lazy load. */
  onRequestConfig(force: boolean): void;
  onSetDisplayName(name: string): void;
  /** Raw base64 PNG (no data: prefix) produced by the canvas resize. */
  onSetAvatar(base64Png: string): void;
  /** Controlled Dialogs have no Radix Trigger; restore the real opener explicitly. */
  onRestoreFocus(): void;
}

const WINDOW_LABELS: Array<{ key: UsageWindowKey; label: string }> = [
  { key: 'today', label: '今日' },
  { key: 'last7Days', label: '7 天' },
  { key: 'last30Days', label: '30 天' }
];

const AVATAR_TARGET_SIZE = 256;
const HEATMAP_DAYS = 365;
const HEATMAP_COLUMNS = 53;
const HEATMAP_ROWS = 7;
const HEATMAP_SLOTS = HEATMAP_COLUMNS * HEATMAP_ROWS;
const DAY_MS = 24 * 60 * 60 * 1000;
const COUNT_FORMATTER = new Intl.NumberFormat('zh-CN');

function formatCount(value: number): string {
  return COUNT_FORMATTER.format(value);
}

interface CivilDate {
  year: number;
  month: number;
  day: number;
}

function parseCivilDate(value: string): CivilDate | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (
    date.getUTCFullYear() !== year
    || date.getUTCMonth() !== month - 1
    || date.getUTCDate() !== day
  ) {
    return null;
  }
  return { year, month, day };
}

function civilDateAt(start: string, offset: number): CivilDate | null {
  const parsed = parseCivilDate(start);
  if (!parsed) return null;
  const date = new Date(Date.UTC(parsed.year, parsed.month - 1, parsed.day) + offset * DAY_MS);
  return {
    year: date.getUTCFullYear(),
    month: date.getUTCMonth() + 1,
    day: date.getUTCDate()
  };
}

function formatCivilDate(date: CivilDate | null): string {
  return date ? `${date.year}年${date.month}月${date.day}日` : '日期未知';
}

function mondayOffset(start: string): number {
  const parsed = parseCivilDate(start);
  if (!parsed) return 0;
  const weekday = new Date(Date.UTC(parsed.year, parsed.month - 1, parsed.day)).getUTCDay();
  return (weekday + 6) % 7;
}

function profileInitial(displayName: string): string {
  const trimmed = displayName.trim();
  return trimmed ? Array.from(trimmed)[0].toUpperCase() : '?';
}

/** Log-relative buckets keep one spike from flattening the rest of the year. */
function heatLevel(value: number, maximum: number): number {
  if (value <= 0 || maximum <= 0) return 0;
  return Math.max(1, Math.floor((4 * Math.log1p(value)) / Math.log1p(maximum)));
}

/** Scale + center-crop the picked image to a 256px square PNG and hand the
 * raw base64 (no data: prefix) to the callback. */
function processAvatarFile(file: File, onDone: (base64Png: string) => void): void {
  const url = URL.createObjectURL(file);
  const image = new Image();
  image.onload = () => {
    try {
      const canvas = document.createElement('canvas');
      canvas.width = AVATAR_TARGET_SIZE;
      canvas.height = AVATAR_TARGET_SIZE;
      const context = canvas.getContext('2d');
      if (!context || !image.width || !image.height) return;
      const side = Math.min(image.width, image.height);
      const sx = (image.width - side) / 2;
      const sy = (image.height - side) / 2;
      context.drawImage(image, sx, sy, side, side, 0, 0, AVATAR_TARGET_SIZE, AVATAR_TARGET_SIZE);
      const dataUrl = canvas.toDataURL('image/png');
      const base64 = dataUrl.split(',')[1] ?? '';
      if (base64) onDone(base64);
    } finally {
      URL.revokeObjectURL(url);
    }
  };
  image.onerror = () => URL.revokeObjectURL(url);
  image.src = url;
}

const REASON_TEXT: Record<UsageGapReason, string> = {
  unsupported_source: '当前 Agent 暂不支持用量统计',
  unsupported_format: '此版本暂不支持用量统计',
  missing_session_logs: '未找到会话用量记录',
  ambiguous_session_logs: '发现重复会话记录',
  unreadable_logs: '部分用量记录无法读取',
  unmatched_sessions: '部分会话未计入',
  missing_session_id: '部分会话无法关联',
  damaged_thread_files: '部分本地会话记录损坏',
  unregistered_provider: '部分会话所属 Agent 已不可用'
};

function primaryReason(completeness: UsageCompleteness): string {
  const reason = completeness.reasons[0];
  return reason ? REASON_TEXT[reason] : '当前没有可用的精确 Token 记录';
}

function Heatmap({
  dailyTokens,
  startDate,
  timezone,
  unavailable,
  scopeLabel
}: {
  dailyTokens: number[];
  startDate: string;
  timezone: string;
  unavailable: boolean;
  scopeLabel: string;
}): JSX.Element {
  if (unavailable) {
    return (
      <section className="agent-usage-heatmap" data-role="usage-heatmap">
        <h2>过去一年 Token 用量 · {scopeLabel}</h2>
        <div className="agent-usage-heatmap-empty" data-role="usage-heatmap-empty">
          暂无可用的精确 Token 数据
        </div>
      </section>
    );
  }

  const values = dailyTokens.slice(0, HEATMAP_DAYS);
  const maximum = Math.max(0, ...values);
  const annualTotal = values.reduce((sum, value) => sum + value, 0);
  const start = civilDateAt(startDate, 0);
  const end = civilDateAt(startDate, Math.max(0, values.length - 1));
  const leading = mondayOffset(startDate);
  const slots = Array.from({ length: HEATMAP_SLOTS }, (_, slot) => {
    const index = slot - leading;
    return index >= 0 && index < values.length ? index : null;
  });
  const ariaLabel = [
    `过去一年 Token 用量，${scopeLabel}`,
    `${formatCivilDate(start)}至${formatCivilDate(end)}`,
    `共 ${formatCount(annualTotal)} Token`,
    `统计时区 ${timezone || '未知'}`
  ].join('，');

  return (
    <section
      className="agent-usage-heatmap"
      data-role="usage-heatmap"
      role="img"
      tabIndex={0}
      aria-label={ariaLabel}
    >
      <div className="agent-usage-heatmap-heading">
        <h2>过去一年 Token 用量 · {scopeLabel}</h2>
        <span aria-hidden="true">颜色越深，当天 Token 越多</span>
      </div>
      <div className="agent-usage-heatmap-grid" aria-hidden="true">
        {slots.map((index, slot) => {
          if (index === null) {
            return <span key={`blank-${slot}`} className="agent-usage-heatmap-placeholder" />;
          }
          const value = values[index] ?? 0;
          return (
            <span
              key={index}
              className="agent-usage-heatmap-cell"
              data-role="usage-heatmap-cell"
              data-index={index}
              data-level={heatLevel(value, maximum)}
              title={`${formatCivilDate(civilDateAt(startDate, index))} · ${formatCount(value)} Token`}
            />
          );
        })}
      </div>
    </section>
  );
}

function ProviderUsageList({
  providers,
  windowKey,
  selectedProviderKey,
  overallAvailable,
  onSelect
}: {
  providers: ProviderUsageReport[];
  windowKey: UsageWindowKey;
  selectedProviderKey: string | null;
  overallAvailable: boolean;
  onSelect(providerKey: string | null): void;
}): JSX.Element {
  return (
    <section className="agent-usage-providers" data-role="usage-providers">
      <div className="agent-usage-providers-heading">
        <h2>按 Agent</h2>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="agent-usage-all-provider"
          data-role="usage-all-provider"
          aria-pressed={selectedProviderKey === null}
          disabled={!overallAvailable}
          title={overallAvailable ? '查看全部 Agent' : '所有 Agent 完整时才可查看合计'}
          onClick={() => onSelect(null)}
        >
          全部
        </Button>
      </div>
      <div className="agent-usage-provider-list" role="group" aria-label="选择年度用量 Agent">
        {providers.map((provider) => {
          const completeness = provider.completeness;
          const unavailable = completeness.status === 'unavailable';
          const total = provider[windowKey].totalTokens;
          const reason = primaryReason(completeness);
          const value = unavailable
            ? `— · ${reason}`
            : completeness.status === 'partial'
              ? `已记录 ${formatCount(total)} Token`
              : `${formatCount(total)} Token`;
          return (
            <Button
              key={provider.providerKey}
              type="button"
              variant="ghost"
              className="agent-usage-provider-row"
              data-role="usage-provider-row"
              aria-pressed={selectedProviderKey === provider.providerKey}
              disabled={unavailable}
              title={completeness.status === 'available' ? provider.displayName : reason}
              onClick={() => onSelect(provider.providerKey)}
            >
              <span className="agent-usage-provider-icon">
                <ProviderIcon iconKey={provider.iconKey} />
              </span>
              <span className="agent-usage-provider-name">{provider.displayName}</span>
              <span
                className="agent-usage-provider-value"
                data-status={completeness.status}
              >
                {value}
              </span>
            </Button>
          );
        })}
      </div>
    </section>
  );
}

function ProfileCard({
  state,
  onSetDisplayName,
  onSetAvatar
}: {
  state: UsageState;
  onSetDisplayName(name: string): void;
  onSetAvatar(base64Png: string): void;
}): JSX.Element {
  const profile = state.profile;
  const fileRef = useRef<HTMLInputElement | null>(null);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(profile.displayName);

  // A profile update landing mid-edit must not clobber the user's draft.
  useEffect(() => {
    if (!editing) setDraft(profile.displayName);
  }, [editing, profile.displayName]);

  const commit = (): void => {
    setEditing(false);
    const next = draft.trim();
    if (next && next !== profile.displayName) onSetDisplayName(next);
  };

  const onNameKeyDown = (event: KeyboardEvent<HTMLInputElement>): void => {
    if (event.key === 'Enter') {
      event.preventDefault();
      commit();
    } else if (event.key === 'Escape') {
      setDraft(profile.displayName);
      setEditing(false);
    }
  };

  return (
    <div className="agent-usage-profile" data-role="usage-profile">
      <button
        type="button"
        className="agent-usage-avatar-button"
        data-role="usage-avatar-button"
        aria-label="更换头像"
        onClick={() => fileRef.current?.click()}
      >
        {profile.avatarDataUrl ? (
          <img className="agent-usage-avatar" src={profile.avatarDataUrl} alt="" />
        ) : (
          <span className="agent-usage-avatar agent-usage-avatar-fallback" aria-hidden="true">
            {profileInitial(profile.displayName)}
          </span>
        )}
      </button>
      <input
        ref={fileRef}
        type="file"
        accept="image/png,image/jpeg"
        className="agent-usage-avatar-input"
        data-role="usage-avatar-input"
        aria-hidden="true"
        tabIndex={-1}
        onChange={(event) => {
          const file = event.target.files?.[0];
          if (file) processAvatarFile(file, onSetAvatar);
          event.target.value = '';
        }}
      />
      {editing ? (
        <input
          className="agent-usage-name-input"
          data-role="usage-profile-name-input"
          aria-label="显示名"
          value={draft}
          maxLength={32}
          autoFocus
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={onNameKeyDown}
          onBlur={commit}
        />
      ) : (
        <button
          type="button"
          className="agent-usage-name"
          data-role="usage-profile-name"
          aria-label="编辑显示名"
          onClick={() => setEditing(true)}
        >
          {profile.displayName || '未命名'}
        </button>
      )}
    </div>
  );
}

export function UsagePanel(props: UsagePanelProps): JSX.Element {
  const state = props.state;
  const [windowKey, setWindowKey] = useState<UsageWindowKey>('today');
  const [selectedProviderKey, setSelectedProviderKey] = useState<string | null>(null);
  const activeTab = state.activeTab;
  const report = state.report;
  const selected = report ? report[windowKey] : null;
  const overallAvailable = state.completeness?.status === 'available';
  const usableProviders = report?.providers.filter(
    provider => provider.completeness.status !== 'unavailable'
  ) ?? [];
  const selectedProvider = selectedProviderKey
    ? usableProviders.find(provider => provider.providerKey === selectedProviderKey) ?? null
    : overallAvailable
      ? null
      : usableProviders[0] ?? null;

  useEffect(() => {
    if (!report) return;
    if (selectedProviderKey === null && overallAvailable) return;
    const nextUsableProviders = report.providers.filter(
      provider => provider.completeness.status !== 'unavailable'
    );
    const remainsUsable = nextUsableProviders.some(
      provider => provider.providerKey === selectedProviderKey
    );
    if (!remainsUsable) {
      setSelectedProviderKey(
        overallAvailable ? null : nextUsableProviders[0]?.providerKey ?? null
      );
    }
  }, [overallAvailable, report, selectedProviderKey]);

  const heatmapSeries = selectedProvider?.dailyTokens ?? report?.dailyTokens ?? [];
  const heatmapScope = selectedProvider?.displayName ?? '全部 Agent';
  const heatmapUnavailable = !selectedProvider && !overallAvailable;

  const selectTab = (tab: UsagePanelTab): void => {
    props.onSelectTab(tab);
  };

  return (
    <Dialog open={state.panelOpen} onOpenChange={props.onOpenChange}>
      <DialogContent
        className="agent-usage-panel max-h-[85vh] gap-0 sm:max-w-[640px]"
        data-role="usage-panel"
        data-agent-font-surface=""
        onCloseAutoFocus={(event) => {
          event.preventDefault();
          props.onRestoreFocus();
        }}
      >
        <DialogTitle className="agent-usage-title">用量与配置</DialogTitle>
        <DialogDescription className="sr-only">
          全局用户资料、各 Agent 用量报告与只读用户级配置
        </DialogDescription>

        <div className="agent-usage-panel-scroll">
          <ProfileCard
            state={state}
            onSetDisplayName={props.onSetDisplayName}
            onSetAvatar={props.onSetAvatar}
          />

          <div
            className="agent-usage-window-switch agent-usage-tab-switch"
            role="tablist"
            aria-label="面板页签"
            data-role="usage-tab-switch"
          >
            <button
              type="button"
              role="tab"
              data-role="usage-tab"
              data-tab="usage"
              aria-selected={activeTab === 'usage'}
              onClick={() => selectTab('usage')}
            >
              用量
            </button>
            <button
              type="button"
              role="tab"
              data-role="usage-tab"
              data-tab="config"
              aria-selected={activeTab === 'config'}
              onClick={() => selectTab('config')}
            >
              配置
            </button>
          </div>

          {activeTab === 'config' ? (
            <ConfigPanel
              state={state}
              onRefresh={() => props.onRequestConfig(true)}
              onRetry={() => props.onRequestConfig(false)}
            />
          ) : (
            <>
              <div className="agent-usage-toolbar">
                <div className="agent-usage-window-switch" role="group" aria-label="统计窗口">
                  {WINDOW_LABELS.map((entry) => (
                    <button
                      key={entry.key}
                      type="button"
                      data-role="usage-window-switch"
                      data-window={entry.key}
                      aria-pressed={windowKey === entry.key}
                      onClick={() => setWindowKey(entry.key)}
                    >
                      {entry.label}
                    </button>
                  ))}
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  data-role="usage-refresh"
                  aria-label="刷新用量"
                  disabled={state.status === 'loading'}
                  onClick={props.onRefresh}
                >
                  <RefreshCwIcon className="size-3.5" aria-hidden="true" />
                  刷新
                </Button>
              </div>

              {state.status === 'error' ? (
                <div className="agent-usage-error" data-role="usage-error" role="alert">
                  <span>{state.errorText}</span>
                  <Button variant="outline" size="sm" data-role="usage-retry" onClick={props.onRetry}>
                    重试
                  </Button>
                </div>
              ) : null}

              {state.status === 'loading' && !report ? (
                <div className="agent-usage-loading" data-role="usage-loading">
                  正在统计…
                </div>
              ) : null}

              {report && selected && state.completeness ? (
                <>
                  <section className="agent-usage-overview" data-role="usage-overview">
                    <div className="agent-usage-overview-label">
                      <span>Token 总量</span>
                    </div>
                    <div
                      className="agent-usage-total"
                      data-role="usage-total-tokens"
                      aria-live="polite"
                    >
                      {formatCount(selected.totalTokens)}
                    </div>
                    {state.completeness.status !== 'available' ? (
                      <p className="agent-usage-overview-note" role="status">
                        统计不完整；当前总量仅包含已读取到的精确 Token。
                      </p>
                    ) : null}
                  </section>
                  <ProviderUsageList
                    providers={report.providers}
                    windowKey={windowKey}
                    selectedProviderKey={selectedProvider?.providerKey ?? null}
                    overallAvailable={overallAvailable}
                    onSelect={setSelectedProviderKey}
                  />
                  <Heatmap
                    dailyTokens={heatmapSeries}
                    startDate={report.heatmapStartDate}
                    timezone={state.timezone}
                    unavailable={heatmapUnavailable}
                    scopeLabel={heatmapScope}
                  />
                </>
              ) : null}
            </>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
