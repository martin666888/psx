// ConfigPanel.tsx — read-only provider config for the Usage panel「配置」tab.
// Secondary Agent tabs switch which provider is shown (no waterfall of cards).
// Facts and notes render as plain text (never HTML/Markdown/auto-link).

import { useEffect, useRef, useState, type JSX } from 'react';
import { RefreshCwIcon } from 'lucide-react';
import { ProviderIcon } from '../components/ProviderIcon.js';
import { Button } from '../components/ui/button.js';
import type {
  ConfigProviderState,
  ProviderConfigReport,
  UsageState
} from '../contracts/agent-usage.js';

export interface ConfigPanelProps {
  state: UsageState;
  onRefresh(): void;
  onRetry(): void;
}

const STATE_LABEL: Record<ConfigProviderState, string> = {
  available: '可用',
  partial: '部分',
  unavailable: '不可用'
};

/** Local HH:MM for the cached-report timestamp; '' when unparsable. */
function formatUpdatedAt(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
}

function StateBadge({ state }: { state: ConfigProviderState }): JSX.Element {
  return (
    <span className="agent-config-state" data-state={state} data-role="config-state">
      {STATE_LABEL[state]}
    </span>
  );
}

function ProviderConfigDetail({
  provider,
  slideDirection
}: {
  provider: ProviderConfigReport;
  /** Slide-in direction for the provider-switch transition. */
  slideDirection: 'forward' | 'back';
}): JSX.Element {
  return (
    <article
      className="agent-config-card"
      data-role="config-provider-card"
      data-provider={provider.providerKey}
      data-state={provider.state}
      data-slide={slideDirection}
    >
      <header className="agent-config-card-header">
        <span className="agent-config-card-icon" aria-hidden="true">
          <ProviderIcon iconKey={provider.iconKey} />
        </span>
        <h3 className="agent-config-card-title">{provider.displayName}</h3>
        <StateBadge state={provider.state} />
      </header>

      {provider.notes.length > 0 ? (
        <ul className="agent-config-notes" data-role="config-notes">
          {provider.notes.map((note) => (
            <li key={note}>{note}</li>
          ))}
        </ul>
      ) : null}

      {provider.facts.length > 0 ? (
        <dl className="agent-config-facts" data-role="config-facts">
          {provider.facts.map((fact) => (
            <div key={`${fact.label}:${fact.value}`} className="agent-config-fact-row">
              <dt>{fact.label}</dt>
              <dd>{fact.value}</dd>
            </div>
          ))}
        </dl>
      ) : null}

      {provider.models.length > 0 ? (
        <section className="agent-config-section" data-role="config-models">
          <h4>模型</h4>
          <ul>
            {provider.models.map((model) => (
              <li key={model.id}>
                <span className="agent-config-mono">{model.name || model.id}</span>
                {model.baseUrl ? (
                  <span className="agent-config-muted"> · {model.baseUrl}</span>
                ) : null}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {provider.mcpServers.length > 0 ? (
        <section className="agent-config-section" data-role="config-mcp">
          <h4>MCP</h4>
          <ul className="agent-config-mcp-list">
            {provider.mcpServers.map((mcp) => (
              <li key={mcp.name} data-enabled={mcp.enabled ? 'true' : 'false'}>
                <div className="agent-config-mcp-row">
                  <span className="agent-config-transport" data-role="config-mcp-transport">
                    {mcp.transport}
                  </span>
                  <span className="agent-config-mcp-name">{mcp.name}</span>
                  {!mcp.enabled ? (
                    <span className="agent-config-muted">已禁用</span>
                  ) : null}
                </div>
                {mcp.target ? (
                  <div className="agent-config-mono agent-config-mcp-target">{mcp.target}</div>
                ) : null}
                {mcp.envKeys.length > 0 || mcp.headerKeys.length > 0 ? (
                  <div className="agent-config-masked-keys">
                    {mcp.envKeys.map((key) => (
                      <span key={`env:${key}`} title="环境变量键（值已隐藏）">
                        env:{key}
                      </span>
                    ))}
                    {mcp.headerKeys.map((key) => (
                      <span key={`hdr:${key}`} title="请求头键（值已隐藏）">
                        header:{key}
                      </span>
                    ))}
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {provider.skills.length > 0 ? (
        <section className="agent-config-section" data-role="config-skills">
          <h4>Skills</h4>
          <div className="agent-config-skills">
            {provider.skills.map((skill) => (
              <span key={skill.name} className="agent-config-skill-chip">
                {skill.name}
              </span>
            ))}
          </div>
        </section>
      ) : null}
    </article>
  );
}

function pickProviderKey(
  providers: ProviderConfigReport[],
  preferred: string
): string {
  if (providers.some((provider) => provider.providerKey === preferred)) {
    return preferred;
  }
  return providers[0]?.providerKey ?? '';
}

export function ConfigPanel(props: ConfigPanelProps): JSX.Element {
  const { state } = props;
  const report = state.configReport;
  const providers = report?.providers ?? [];
  const [selectedKey, setSelectedKey] = useState('');

  useEffect(() => {
    if (!report) {
      setSelectedKey('');
      return;
    }
    setSelectedKey((current) => pickProviderKey(report.providers, current));
  }, [report]);

  const selected =
    providers.find((provider) => provider.providerKey === selectedKey) ?? null;

  // Direction-aware slide: comparing against the previous index tells whether
  // the new card enters from the right (forward) or the left (back). The ref
  // updates after commit, so the render right after a switch still sees the
  // old index. The keyed remount below replays the CSS animation per switch.
  const selectedIndex = providers.findIndex(
    (provider) => provider.providerKey === selectedKey
  );
  const prevIndexRef = useRef(-1);
  const slideDirection = selectedIndex >= prevIndexRef.current ? 'forward' : 'back';
  useEffect(() => {
    prevIndexRef.current = selectedIndex;
  }, [selectedIndex]);

  return (
    <div className="agent-config-panel" data-role="config-panel">
      <div className="agent-usage-toolbar">
        <div className="agent-config-toolbar-label">用户级配置（只读）</div>
        <div className="agent-config-toolbar-side">
          {state.configGeneratedAt ? (
            <span className="agent-config-toolbar-updated" data-role="config-updated-at">
              更新于 {formatUpdatedAt(state.configGeneratedAt)}
            </span>
          ) : null}
          <Button
            variant="outline"
            size="sm"
            data-role="config-refresh"
            aria-label="刷新配置"
            disabled={state.configStatus === 'loading'}
            onClick={props.onRefresh}
          >
            <RefreshCwIcon className="size-3.5" aria-hidden="true" />
            刷新
          </Button>
        </div>
      </div>

      {state.configStatus === 'error' ? (
        <div className="agent-usage-error" data-role="config-error" role="alert">
          <span>{state.configErrorText}</span>
          <Button variant="outline" size="sm" data-role="config-retry" onClick={props.onRetry}>
            重试
          </Button>
        </div>
      ) : null}

      {state.configStatus === 'loading' && !report ? (
        <div className="agent-usage-loading" data-role="config-loading">
          正在读取配置…
        </div>
      ) : null}

      {report ? (
        providers.length === 0 ? (
          <div className="agent-usage-loading">暂无配置可展示</div>
        ) : (
          <div className="agent-config-body" data-role="config-body">
            <div
              className="agent-usage-window-switch agent-config-provider-switch"
              role="tablist"
              aria-label="选择 Agent"
              data-role="config-provider-switch"
            >
              {providers.map((provider) => (
                <button
                  key={provider.providerKey}
                  type="button"
                  role="tab"
                  className="agent-config-provider-tab"
                  data-role="config-provider-tab"
                  data-provider={provider.providerKey}
                  aria-selected={provider.providerKey === selectedKey}
                  aria-pressed={provider.providerKey === selectedKey}
                  title={provider.displayName}
                  onClick={() => setSelectedKey(provider.providerKey)}
                >
                  <span className="agent-config-provider-tab-icon" aria-hidden="true">
                    <ProviderIcon iconKey={provider.iconKey} />
                  </span>
                  <span className="agent-config-provider-tab-label">
                    {provider.displayName}
                  </span>
                </button>
              ))}
            </div>

            {selected ? (
              <ProviderConfigDetail
                key={selected.providerKey}
                provider={selected}
                slideDirection={slideDirection}
              />
            ) : null}
          </div>
        )
      ) : null}
    </div>
  );
}
