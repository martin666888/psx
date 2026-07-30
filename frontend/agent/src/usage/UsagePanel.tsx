// UsagePanel.tsx — the global Usage panel dialog (profile + usage report).
//
// One singleton Dialog for the whole process, opened from the History dock
// footer. Everything rendered here is data-driven:
//   - the overview reads the selected window (today/last7Days/last30Days)
//     straight from the report; the per-provider detail switches with it.
//   - provider sections render purely by which fields exist (exactUsage →
//     model table, contextSnapshots → snapshot list). providerKey is used as
//     a React key and label only — NEVER for conditional rendering.
//   - context snapshots never join the window totals (backend contract).

import { useEffect, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import { RefreshCwIcon } from 'lucide-react';
import { Button } from '../components/ui/button.js';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogTitle
} from '../components/ui/dialog.js';
import type {
  UsageProviderSection,
  UsageSourceStatus,
  UsageState,
  UsageWindow,
  UsageWindowKey
} from '../contracts/agent-usage.js';

export interface UsagePanelProps {
  state: UsageState;
  onOpenChange(open: boolean): void;
  /** Refresh button: bypasses the backend cache. */
  onRefresh(): void;
  /** Error retry: an ordinary cached load. */
  onRetry(): void;
  onSetDisplayName(name: string): void;
  /** Raw base64 PNG (no data: prefix) produced by the canvas resize. */
  onSetAvatar(base64Png: string): void;
}

const WINDOW_LABELS: Array<{ key: UsageWindowKey; label: string }> = [
  { key: 'today', label: '今日' },
  { key: 'last7Days', label: '7 天' },
  { key: 'last30Days', label: '30 天' }
];

const CACHE_RATE_TOOLTIP = '缓存命中率 = cacheRead / (input + cacheRead + cacheCreate)';
const AVATAR_TARGET_SIZE = 256;

function formatCount(value: number): string {
  return value.toLocaleString('en-US');
}

function totalTokens(window: UsageWindow): number {
  const t = window.tokens;
  return t.input + t.output + t.cacheRead + t.cacheCreation;
}

function formatRate(rate: number | null): string {
  return rate === null ? '—' : (rate * 100).toFixed(1) + '%';
}

function profileInitial(displayName: string): string {
  const trimmed = displayName.trim();
  return trimmed ? Array.from(trimmed)[0].toUpperCase() : '?';
}

/** Bucket a daily activity count into one of 5 heatmap intensity levels. */
function heatLevel(count: number): number {
  if (count <= 0) return 0;
  if (count === 1) return 1;
  if (count <= 3) return 2;
  if (count <= 6) return 3;
  return 4;
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

function sourceForSection(
  section: UsageProviderSection,
  sources: UsageSourceStatus[]
): UsageSourceStatus | null {
  return sources.find((source) => source.key === section.sourceKey) ?? null;
}

function SourceBadge({ source }: { source: UsageSourceStatus | null }): JSX.Element | null {
  if (!source || source.status === 'available') return null;
  const parts: string[] = [];
  if (source.detail) parts.push(source.detail);
  if (source.status === 'partial') {
    if (source.skippedFiles > 0) parts.push(`跳过 ${source.skippedFiles} 个文件`);
    if (source.badLines > 0) parts.push(`${source.badLines} 行无法解析`);
  }
  return (
    <span
      className="agent-usage-source-badge"
      data-role="usage-source-badge"
      data-status={source.status}
    >
      {source.status === 'partial' ? '数据不完整' : '数据不可用'}
      {parts.length > 0 ? '：' + parts.join('；') : ''}
    </span>
  );
}

function ProviderSectionView({
  section,
  sources
}: {
  section: UsageProviderSection;
  sources: UsageSourceStatus[];
}): JSX.Element {
  return (
    <section
      className="agent-usage-provider"
      data-role="usage-provider-section"
      data-provider-key={section.providerKey}
    >
      <header className="agent-usage-provider-header">
        <span className="agent-usage-provider-name">{section.providerKey}</span>
        <SourceBadge source={sourceForSection(section, sources)} />
      </header>
      {section.exactUsage ? (
        <table className="agent-usage-model-table" data-role="usage-model-table">
          <thead>
            <tr>
              <th scope="col">模型</th>
              <th scope="col">输入</th>
              <th scope="col">输出</th>
              <th scope="col">缓存读</th>
              <th scope="col">缓存写</th>
            </tr>
          </thead>
          <tbody>
            {section.exactUsage.modelRows.map((row) => (
              <tr key={row.model} data-role="usage-model-row">
                <td>{row.model}</td>
                <td>{formatCount(row.input)}</td>
                <td>{formatCount(row.output)}</td>
                <td>{formatCount(row.cacheRead)}</td>
                <td>{formatCount(row.cacheCreation)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      {section.contextSnapshots ? (
        <div className="agent-usage-snapshots" data-role="usage-context-snapshots">
          <div className="agent-usage-snapshots-title">上下文快照（不计入合计）</div>
          <ul>
            {section.contextSnapshots.map((snapshot, index) => (
              <li key={index} data-role="usage-context-snapshot">
                <span className="agent-usage-snapshot-title">{snapshot.threadTitle}</span>
                <span className="agent-usage-snapshot-tokens">
                  {snapshot.usedTokens === null ? '—' : formatCount(snapshot.usedTokens)}
                  {snapshot.windowTokens !== null
                    ? ' / ' + formatCount(snapshot.windowTokens)
                    : ''}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </section>
  );
}

function Heatmap({ heatmap }: { heatmap: number[] }): JSX.Element {
  const activeDays = heatmap.filter((count) => count > 0).length;
  return (
    <div
      className="agent-usage-heatmap"
      data-role="usage-heatmap"
      role="img"
      tabIndex={0}
      aria-label={`过去 365 天活跃 ${activeDays} 天`}
    >
      {/* One focus/reader stop for the whole grid: cells are purely visual. */}
      <div className="agent-usage-heatmap-grid" aria-hidden="true">
        {heatmap.map((count, index) => (
          <span key={index} className="agent-usage-heatmap-cell" data-level={heatLevel(count)} />
        ))}
      </div>
    </div>
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
  const report = state.report;
  const selected = report ? report[windowKey] : null;

  return (
    <Dialog open={state.panelOpen} onOpenChange={props.onOpenChange}>
      <DialogContent
        className="agent-usage-panel max-h-[85vh] gap-0 overflow-y-auto sm:max-w-[680px]"
        data-role="usage-panel"
      >
        <DialogTitle>用量</DialogTitle>
        <DialogDescription className="sr-only">全局用户资料与各 Agent 用量报告</DialogDescription>

        <ProfileCard
          state={state}
          onSetDisplayName={props.onSetDisplayName}
          onSetAvatar={props.onSetAvatar}
        />

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

        {report && selected ? (
          <>
            <dl className="agent-usage-overview" data-role="usage-overview">
              <div>
                <dt>Token 总量</dt>
                <dd data-role="usage-total-tokens">{formatCount(totalTokens(selected))}</dd>
              </div>
              <div>
                <dt>输出</dt>
                <dd data-role="usage-output-tokens">{formatCount(selected.tokens.output)}</dd>
              </div>
              <div title={CACHE_RATE_TOOLTIP}>
                <dt>缓存命中率</dt>
                <dd data-role="usage-cache-rate">{formatRate(selected.cacheHitRate)}</dd>
              </div>
              <div>
                <dt>活跃会话</dt>
                <dd data-role="usage-active-threads">{formatCount(selected.activeThreads)}</dd>
              </div>
            </dl>

            {selected.providerSections.map((section) => (
              <ProviderSectionView
                key={section.providerKey}
                section={section}
                sources={state.sources}
              />
            ))}

            <Heatmap heatmap={report.heatmap} />
          </>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}
