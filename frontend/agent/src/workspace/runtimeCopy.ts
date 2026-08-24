// runtimeCopy.ts — fixed backend codes → display sentences for the
// runtime_status / runtime_update_status bridge events. C# sends only the
// stable messageCode (Models/RuntimeStatusCode.cs); composed sentences and
// raw npm output never cross the bridge.

export const RUNTIME_STATUS_COPY: Readonly<Record<string, string>> = {
  'runtime.not_installed': 'Agent runtime is not installed.',
  'runtime.ready': 'Agent runtime is ready.',
  'runtime.preparing_install': 'Preparing to install the Agent runtime…',
  'runtime.install_succeeded': 'Agent runtime installed successfully.',
  'runtime.cancelling_install': 'Cancelling Agent runtime installation…',
  'runtime.install_cancelled': 'Agent runtime installation was cancelled.',
  'runtime.network_unavailable': 'Download failed. Check the network connection and retry.',
  'runtime.install_failed': 'Agent runtime installation failed.'
};

export function runtimeStatusLabel(code: string): string {
  return RUNTIME_STATUS_COPY[code] || '';
}

export const RUNTIME_UPDATE_FAILURE_COPY: Readonly<Record<string, string>> = {
  'update.failed': 'The update did not complete. Click to retry.',
  'update.network_unavailable':
    'The update failed — the network is unavailable. Check the connection and retry.',
  'runtime.transcript_read_only': 'This saved transcript is read-only.'
};

export function runtimeUpdateFailureLabel(code: string): string {
  return RUNTIME_UPDATE_FAILURE_COPY[code] || '';
}
