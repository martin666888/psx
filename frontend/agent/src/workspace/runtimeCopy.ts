// runtimeCopy.ts — backend messageCode → display copy through the shared
// i18n instance (agent namespace). C# sends only stable codes
// (Models/RuntimeStatusCode.cs); composed sentences never cross the bridge.

import { t } from '../../../webview/src/i18n.js';

const RUNTIME_STATUS_KEY: Readonly<Record<string, string>> = {
  'runtime.not_installed': 'runtime.notInstalled',
  'runtime.ready': 'runtime.ready',
  'runtime.preparing_install': 'runtime.preparingInstall',
  'runtime.install_succeeded': 'runtime.installSucceeded',
  'runtime.cancelling_install': 'runtime.cancellingInstall',
  'runtime.install_cancelled': 'runtime.installCancelled',
  'runtime.network_unavailable': 'runtime.networkUnavailable',
  'runtime.install_failed': 'runtime.installFailed'
};

export function runtimeStatusLabel(code: string): string {
  const key = RUNTIME_STATUS_KEY[code];
  return key ? t(key, { ns: 'agent', defaultValue: '' }) : '';
}

const RUNTIME_UPDATE_FAILURE_KEY: Readonly<Record<string, string>> = {
  'update.failed': 'runtime.updateFailed',
  'update.network_unavailable': 'runtime.updateNetworkUnavailable',
  'runtime.transcript_read_only': 'runtime.transcriptReadOnly'
};

export function runtimeUpdateFailureLabel(code: string): string {
  const key = RUNTIME_UPDATE_FAILURE_KEY[code];
  return key ? t(key, { ns: 'agent', defaultValue: '' }) : '';
}
