// Shared Vitest setup. The old node --test runner had no global setup; keep
// this minimal so tests stay self-describing. React act() support is enabled
// per-file by the tests that render React trees, exactly as before.
import { afterAll, afterEach } from 'vitest';
import { act } from 'react';
import { disposeActiveAgentRuntime } from './agentHarness.js';
import { registerNamespace } from '../../../frontend/webview/src/i18n.js';
import agentZhHans from '../../../frontend/agent/src/locales/zh-Hans/agent.json';
import agentZhHant from '../../../frontend/agent/src/locales/zh-Hant/agent.json';
import agentEn from '../../../frontend/agent/src/locales/en/agent.json';
import agentJa from '../../../frontend/agent/src/locales/ja/agent.json';
import settingsZhHans from '../../../frontend/agent/src/usage/locales/zh-Hans/settings.json';
import settingsZhHant from '../../../frontend/agent/src/usage/locales/zh-Hant/settings.json';
import settingsEn from '../../../frontend/agent/src/usage/locales/en/settings.json';
import settingsJa from '../../../frontend/agent/src/usage/locales/ja/settings.json';

// Tests that mount islands directly (bypassing entry.ts / usageIsland.ts)
// still need the lazy namespaces; register them once for the whole file run.
registerNamespace('agent', {
  'zh-Hans': agentZhHans,
  'zh-Hant': agentZhHant,
  en: agentEn,
  ja: agentJa
});
registerNamespace('settings', {
  'zh-Hans': settingsZhHans,
  'zh-Hant': settingsZhHant,
  en: settingsEn,
  ja: settingsJa
});

process.env.TZ ||= 'UTC';

// jsdom ships no ResizeObserver; use-stick-to-bottom (AI Elements
// Conversation) needs one. A no-op stub is enough: scroll metrics in jsdom
// are all zero, so stick-to-bottom simply stays at the bottom.
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

// Dispose whatever the test built on the jsdom runtime (AgentApp, brokers,
// islands) and close the window before the next test, so pending broker
// timeout timers and detached DOM trees never accumulate across a file/fork.
afterEach(async () => {
  const previousActEnvironment = globalThis.IS_REACT_ACT_ENVIRONMENT;
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  try {
    await act(async () => {
      disposeActiveAgentRuntime();
    });
    // Coverage keeps transformed code alive until its report is emitted, but
    // disposed jsdom/React graphs must not survive between tests in the same
    // file. Workers are launched with --expose-gc for this bounded cleanup.
    globalThis.gc?.();
  } finally {
    globalThis.IS_REACT_ACT_ENVIRONMENT = previousActEnvironment;
  }
});

// Island loaders import their React module dynamically after a host event.
// A test file may legitimately finish while such an import is still in
// flight; give pending dynamic imports one macrotask window to settle so the
// worker does not tear down its module rpc mid-fetch (noise, not a failure).
afterAll(async () => {
  await new Promise((resolve) => setTimeout(resolve, 50));
});
