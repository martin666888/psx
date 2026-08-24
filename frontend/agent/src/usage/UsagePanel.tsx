// UsagePanel.tsx — the global settings dialog (left nav + section content).
//
// One singleton Dialog for the whole process, opened from PSX 设置. Left-nav
// sections are profile, usage, config and the npm download source; more
// settings can land here later. Usage numbers stay backend-owned: aggregate
// and per-Provider totals plus a 365-day exact-token heatmap. Model / thread
// / parser detail never crosses the public contract.

import { useEffect, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { i18n } from '../../../webview/src/i18n.js';
import { BarChart3Icon, GlobeIcon, RefreshCwIcon, SlidersHorizontalIcon, UserRoundIcon } from 'lucide-react';
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
  SettingsSection,
  UsageCompleteness,
  UsageState,
  UsageWindowKey
} from '../contracts/agent-usage.js';
import { ConfigPanel } from './ConfigPanel.js';
import {
  DSH_REGISTRY_KEYS,
  DSH_REGISTRY_LABELS,
  type DshRegistryKey
} from './settingsRegistry.js';

export interface UsagePanelProps {
  state: UsageState;
  onOpenChange(open: boolean): void;
  onSelectSection(section: SettingsSection): void;
  /** Usage Refresh button: bypasses the backend cache. */
  onRefresh(): void;
  /** Usage error retry: an ordinary cached load. */
  onRetry(): void;
  /** Config Refresh / first lazy load. */
  onRequestConfig(force: boolean): void;
  onSetDisplayName(name: string): void;
  /** Raw base64 PNG (no data: prefix) produced by the canvas resize. */
  onSetAvatar(base64Png: string): void;
  onSetSettingsDraft(registry: DshRegistryKey): void;
  onApplyRegistry(registry: DshRegistryKey): void;
  onSetLocale(mode: string): void;
  /** Controlled Dialogs have no Radix Trigger; restore the real opener explicitly. */
  onRestoreFocus(): void;
}

// Labels resolve through the settings namespace at render time so a
// language switch re-renders the nav without a remount.
const SETTINGS_SECTIONS: Array<{
  id: SettingsSection;
  labelKey: string;
  Icon: typeof UserRoundIcon;
}> = [
  { id: 'profile', labelKey: 'section.profile', Icon: UserRoundIcon },
  { id: 'language', labelKey: 'section.language', Icon: GlobeIcon },
  { id: 'usage', labelKey: 'section.usage', Icon: BarChart3Icon },
  { id: 'config', labelKey: 'section.config', Icon: SlidersHorizontalIcon },
  { id: 'registry', labelKey: 'section.registry', Icon: GlobeIcon }
];

const WINDOW_KEYS: UsageWindowKey[] = ['today', 'last7Days', 'last30Days'];

function windowLabel(key: UsageWindowKey): string {
  return key === 'today'
    ? tStatic('usage.windowToday')
    : key === 'last7Days'
      ? tStatic('usage.window7Days')
      : tStatic('usage.window30Days');
}

const AVATAR_TARGET_SIZE = 256;
const HEATMAP_DAYS = 365;
const HEATMAP_COLUMNS = 53;
const HEATMAP_ROWS = 7;
const HEATMAP_SLOTS = HEATMAP_COLUMNS * HEATMAP_ROWS;
const DAY_MS = 24 * 60 * 60 * 1000;
function formatCount(value: number): string {
  return new Intl.NumberFormat(i18n.resolvedLanguage || 'zh-Hans').format(value);
}

/** Translate from the settings namespace outside component scopes. */
function tStatic(key: string, options?: Record<string, unknown>): string {
  return i18n.t(key, { ns: 'settings', ...options });
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
  if (!date) return tStatic('usage.dateUnknown');
  // Locale-aware civil date (2024年1月5日 / January 5, 2024 / …).
  return new Intl.DateTimeFormat(i18n.resolvedLanguage || 'zh-Hans', {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    timeZone: 'UTC'
  }).format(new Date(Date.UTC(date.year, date.month - 1, date.day)));
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

function primaryReason(completeness: UsageCompleteness): string {
  const reason = completeness.reasons[0];
  return reason
    ? tStatic(`usage.reasons.${reason}`)
    : tStatic('usage.noExactRecords');
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
  const { t } = useTranslation('settings');
  if (unavailable) {
    return (
      <section className="agent-usage-heatmap" data-role="usage-heatmap">
        <h2>{t('usage.heatmapHeading', { scope: scopeLabel })}</h2>
        <div className="agent-usage-heatmap-empty" data-role="usage-heatmap-empty">
          {t('usage.heatmapEmpty')}
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
  const joiner = tStatic('usage.ariaJoin');
  const ariaLabel = [
    tStatic('usage.heatmapHeading', { scope: scopeLabel }),
    tStatic('usage.dateRangeJoin', {
      start: formatCivilDate(start),
      end: formatCivilDate(end)
    }),
    tStatic('usage.totalTokensLine', { count: formatCount(annualTotal) }),
    tStatic('usage.timezoneLine', { timezone: timezone || tStatic('usage.timezoneUnknown') })
  ].join(joiner);

  return (
    <section
      className="agent-usage-heatmap"
      data-role="usage-heatmap"
      role="img"
      tabIndex={0}
      aria-label={ariaLabel}
    >
      <div className="agent-usage-heatmap-heading">
        <h2>{t('usage.heatmapHeading', { scope: scopeLabel })}</h2>
        <span aria-hidden="true">{t('usage.colorHint')}</span>
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
              title={t('usage.cellTitle', {
                date: formatCivilDate(civilDateAt(startDate, index)),
                count: formatCount(value)
              })}
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
  const { t } = useTranslation('settings');
  return (
    <section className="agent-usage-providers" data-role="usage-providers">
      <div className="agent-usage-providers-heading">
        <h2>{t('usage.byAgent')}</h2>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="agent-usage-all-provider"
          data-role="usage-all-provider"
          aria-pressed={selectedProviderKey === null}
          disabled={!overallAvailable}
          title={overallAvailable ? t('usage.viewAllTitle') : t('usage.viewAllBlockedTitle')}
          onClick={() => onSelect(null)}
        >
          {t('usage.allProviders')}
        </Button>
      </div>
      <div className="agent-usage-provider-list" role="group" aria-label={t('usage.providerGroupAria')}>
        {providers.map((provider) => {
          const completeness = provider.completeness;
          const unavailable = completeness.status === 'unavailable';
          const total = provider[windowKey].totalTokens;
          const reason = primaryReason(completeness);
          const amount = t('usage.tokenAmount', { count: formatCount(total) });
          const value = unavailable
            ? t('usage.unavailableValue', { reason })
            : completeness.status === 'partial'
              ? t('usage.recordedPrefix') + amount
              : amount;
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
  const { t } = useTranslation('settings');
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
    <div
      className="agent-settings-profile"
      data-role="usage-profile"
      aria-busy={state.profileSaving || undefined}
    >
      <div className="agent-usage-profile">
        <button
          type="button"
          className="agent-usage-avatar-button"
          data-role="usage-avatar-button"
          aria-label={t('profile.avatarAria')}
          disabled={state.profileSaving}
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
        disabled={state.profileSaving}
        onChange={(event) => {
          const file = event.target.files?.[0];
          if (file) processAvatarFile(file, onSetAvatar);
          event.target.value = '';
        }}
      />
      <div className="agent-settings-profile-copy">
        {editing ? (
          <input
            className="agent-usage-name-input"
            data-role="usage-profile-name-input"
            aria-label={t('profile.nameAria')}
            value={draft}
            maxLength={32}
            autoFocus
            disabled={state.profileSaving}
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={onNameKeyDown}
            onBlur={commit}
          />
        ) : (
          <button
            type="button"
            className="agent-usage-name"
            data-role="usage-profile-name"
            aria-label={t('profile.editNameAria')}
            disabled={state.profileSaving}
            onClick={() => setEditing(true)}
          >
            {profile.displayName || t('profile.unnamed')}
          </button>
        )}
        <p className="agent-settings-note">{t('profile.note')}</p>
      </div>
      </div>
      {state.profileError ? (
        <p className="agent-usage-error" data-role="usage-profile-error" role="alert">
          {state.profileError}
        </p>
      ) : null}
    </div>
  );
}

export function UsagePanel(props: UsagePanelProps): JSX.Element {
  const { t } = useTranslation('settings');
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
  const heatmapScope = selectedProvider?.displayName ?? t('usage.allAgentsScope');
  const heatmapUnavailable = !selectedProvider && !overallAvailable;

    const ignoreSettingsToggle = (event: { preventDefault(): void; target: EventTarget | null }): void => {
    const target = event.target;
    if (target instanceof Element && target.closest('[data-role="app-settings-toggle"]'))
      event.preventDefault();
  };

  return (
    <Dialog open={state.panelOpen} onOpenChange={props.onOpenChange}>
      <DialogContent
        className="agent-usage-panel agent-settings-panel flex flex-col max-h-[min(640px,calc(100vh-2rem))] w-[min(880px,calc(100vw-2rem))] gap-0 p-0 sm:max-w-[min(880px,calc(100vw-2rem))]"
        data-role="usage-panel"
        data-tab={activeTab}
        data-agent-font-surface=""
        onCloseAutoFocus={(event) => {
          event.preventDefault();
          props.onRestoreFocus();
        }}
        onInteractOutside={ignoreSettingsToggle}
        onPointerDownOutside={ignoreSettingsToggle}
      >
        <DialogTitle className="sr-only">{t('title')}</DialogTitle>
        <DialogDescription className="sr-only">
          {t('dialogDescription')}
        </DialogDescription>

        <div className="agent-settings-shell">
        <nav className="agent-settings-nav agent-native-scroll" data-role="settings-nav" aria-label={t('navAria')}>
          <p className="agent-settings-nav-title">{t('title')}</p>
          {SETTINGS_SECTIONS.map((entry) => (
            <button
              key={entry.id}
              type="button"
              className="agent-settings-nav-item"
              data-role="settings-nav-item"
              data-section={entry.id}
              aria-current={activeTab === entry.id ? 'page' : undefined}
              onClick={() => props.onSelectSection(entry.id)}
            >
              <entry.Icon className="size-4" aria-hidden="true" />
              {t(entry.labelKey)}
            </button>
          ))}
        </nav>

        <div className="agent-settings-main">
          <h2 className="agent-settings-heading" data-role="settings-heading">
            {t(`section.${activeTab}`)}
          </h2>
          <div className="agent-usage-panel-scroll agent-settings-body">
            {activeTab === 'profile' ? (
              <ProfileCard
                state={state}
                onSetDisplayName={props.onSetDisplayName}
                onSetAvatar={props.onSetAvatar}
              />
            ) : null}

            {activeTab === 'language' ? (
              <LanguageSection state={state} onSetLocale={props.onSetLocale} />
            ) : null}

            {activeTab === 'config' ? (
              <ConfigPanel
                state={state}
                onRefresh={() => props.onRequestConfig(true)}
                onRetry={() => props.onRequestConfig(false)}
              />
            ) : null}

            {activeTab === 'registry' ? (
              <RegistrySection
                state={state}
                onSelect={props.onSetSettingsDraft}
                onApply={props.onApplyRegistry}
              />
            ) : null}

            {activeTab === 'usage' ? (
              <>
              <div className="agent-usage-toolbar">
                <div className="agent-usage-window-switch" role="group" aria-label={t('usage.windowGroupAria')}>
                  {WINDOW_KEYS.map((key) => (
                    <button
                      key={key}
                      type="button"
                      data-role="usage-window-switch"
                      data-window={key}
                      aria-pressed={windowKey === key}
                      onClick={() => setWindowKey(key)}
                    >
                      {windowLabel(key)}
                    </button>
                  ))}
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  data-role="usage-refresh"
                  aria-label={t('usage.refreshAria')}
                  disabled={state.status === 'loading'}
                  onClick={props.onRefresh}
                >
                  <RefreshCwIcon className="size-3.5" aria-hidden="true" />
                  {t('common.refresh')}
                </Button>
              </div>

              {state.status === 'error' ? (
                <div className="agent-usage-error" data-role="usage-error" role="alert">
                  <span>{state.errorText}</span>
                  <Button variant="outline" size="sm" data-role="usage-retry" onClick={props.onRetry}>
                    {t('common.retry')}
                  </Button>
                </div>
              ) : null}

              {state.status === 'loading' && !report ? (
                <div className="agent-usage-loading" data-role="usage-loading">
                  {t('usage.loading')}
                </div>
              ) : null}

              {report && selected && state.completeness ? (
                <>
                  <section className="agent-usage-overview" data-role="usage-overview">
                    <div className="agent-usage-overview-label">
                      <span>{t('usage.totalLabel')}</span>
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
                        {t('usage.incompleteNote')}
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
            ) : null}
          </div>
        </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function RegistrySection({
  state,
  onSelect,
  onApply
}: {
  state: UsageState;
  onSelect(registry: DshRegistryKey): void;
  onApply(registry: DshRegistryKey): void;
}): JSX.Element {
  const { t } = useTranslation('settings');
  return (
    <div className="agent-settings-registry" data-role="settings-registry">
      <p className="agent-settings-lede">
        {t('registry.lede')}
      </p>
      {DSH_REGISTRY_KEYS.map((key) => (
        <button
          key={key}
          type="button"
          className="agent-settings-choice"
          data-role="settings-registry-choice"
          data-registry={key}
          data-selected={state.settingsDraft === key ? 'true' : 'false'}
          aria-pressed={state.settingsDraft === key}
          onClick={() => onSelect(key)}
        >
          <span className="agent-settings-choice-label">{DSH_REGISTRY_LABELS[key]}</span>
          <span className="agent-settings-choice-note">{key === 'official' ? t('registry.noteOfficial') : t('registry.noteNpmmirror')}</span>
        </button>
      ))}
      {state.settingsError ? (
        <p className="agent-usage-error" data-role="settings-registry-error" role="alert">
          {state.settingsError}
        </p>
      ) : null}
      <Button
        size="sm"
        data-role="settings-registry-apply"
        disabled={state.settingsDraft === state.dshRegistry}
        onClick={() => onApply(state.settingsDraft)}
      >
        {t('common.apply')}
      </Button>
    </div>
  );
}

const LOCALE_MODES: Array<{ mode: string; nameKey: string | null }> = [
  { mode: 'system', nameKey: null },
  { mode: 'zh-Hans', nameKey: 'localeNames.zh-Hans' },
  { mode: 'zh-Hant', nameKey: 'localeNames.zh-Hant' },
  { mode: 'en', nameKey: 'localeNames.en' },
  { mode: 'ja', nameKey: 'localeNames.ja' }
];

/** Language settings: one radio list, native language names, immediate save
 * through app_settings_command set_locale. A failure keeps the previous
 * language and surfaces the fixed error in the current UI language. */
function LanguageSection({
  state,
  onSetLocale
}: {
  state: UsageState;
  onSetLocale(mode: string): void;
}): JSX.Element {
  const { t } = useTranslation('settings');
  const resolvedCode = state.resolvedLocale || 'zh-Hans';
  const resolved =
    state.resolvedLocale === ''
      ? ''
      : t(`language.localeNames.${resolvedCode}`, { defaultValue: resolvedCode });
  const systemResolvedName =
    resolved === '' ? '' : `（${t('language.systemResolvedSuffix', { resolved })}）`;
  return (
    <div className="agent-settings-registry" data-role="settings-language">
      <p className="agent-settings-lede">{t('language.lede')}</p>
      {LOCALE_MODES.map(({ mode, nameKey }) => {
        const label =
          nameKey == null
            ? `${t('language.systemOption')}${systemResolvedName}`
            : t(`language.${nameKey}`);
        return (
          <button
            key={mode}
            type="button"
            className="agent-settings-choice"
            data-role="settings-language-choice"
            data-mode={mode}
            data-selected={state.localeMode === mode ? 'true' : 'false'}
            aria-pressed={state.localeMode === mode}
            onClick={() => {
              if (state.localeMode !== mode) onSetLocale(mode);
            }}
          >
            <span className="agent-settings-choice-label">{label}</span>
          </button>
        );
      })}
      {state.languageError ? (
        <p className="agent-usage-error" data-role="settings-language-error" role="alert">
          {state.languageError}
        </p>
      ) : null}
    </div>
  );
}
