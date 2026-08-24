const BENIGN_RESIZE_OBSERVER_MESSAGES = new Set([
    'ResizeObserver loop completed with undelivered notifications.',
    'ResizeObserver loop limit exceeded'
]);

import { t } from './i18n.js';

const DIAGNOSTIC_ROLE = 'runtime-diagnostic';

export function isBenignResizeObserverMessage(value) {
    return BENIGN_RESIZE_OBSERVER_MESSAGES.has(String(value ?? '').trim());
}

function rejectionMessage(reason) {
    if (reason instanceof Error) return reason.message;
    if (typeof reason === 'string') return reason;
    return 'Unhandled promise rejection';
}

function createDiagnostic(documentRef) {
    const diagnostic = documentRef.createElement('aside');
    diagnostic.className = 'psx-runtime-diagnostic';
    diagnostic.dataset.role = DIAGNOSTIC_ROLE;
    diagnostic.setAttribute('role', 'alert');
    diagnostic.setAttribute('aria-live', 'assertive');

    const message = documentRef.createElement('span');
    message.className = 'psx-runtime-diagnostic-message';
    message.textContent = t('diagnostics.generic');

    const dismiss = documentRef.createElement('button');
    dismiss.type = 'button';
    dismiss.className = 'psx-runtime-diagnostic-dismiss';
    dismiss.textContent = t('diagnostics.dismiss');
    dismiss.setAttribute('aria-label', t('diagnostics.dismissAria'));
    dismiss.addEventListener('click', () => diagnostic.remove());

    diagnostic.append(message, dismiss);
    return diagnostic;
}

/**
 * Installs the one process-page error boundary for the always-loaded shell.
 * Component failures remain owned by their React error boundaries. Browser
 * ResizeObserver delivery warnings are explicitly classified as non-fatal;
 * every other uncaught error keeps its normal console diagnostics and receives
 * one sanitized, dismissible user-facing notice with no stack or path data.
 */
export function installRuntimeDiagnostics(windowRef = window, documentRef = document) {
    const previous = windowRef.__psxRuntimeDiagnostics;
    if (previous?.dispose) return previous.dispose;

    const seen = new Set();

    const show = (kind, message) => {
        const fingerprint = `${kind}:${String(message ?? '')}`;
        if (seen.has(fingerprint)) return;
        seen.add(fingerprint);

        if (documentRef.querySelector(`[data-role="${DIAGNOSTIC_ROLE}"]`)) return;
        const mount = documentRef.body ?? documentRef.documentElement;
        mount?.appendChild(createDiagnostic(documentRef));
    };

    const onError = (event) => {
        if (isBenignResizeObserverMessage(event.message)) {
            event.preventDefault();
            return;
        }
        show('error', event.message || 'Uncaught error');
    };

    const onUnhandledRejection = (event) => {
        const message = rejectionMessage(event.reason);
        if (isBenignResizeObserverMessage(message)) {
            event.preventDefault();
            return;
        }
        show('promise', message);
    };

    windowRef.addEventListener('error', onError);
    windowRef.addEventListener('unhandledrejection', onUnhandledRejection);

    const state = {
        dispose() {
            windowRef.removeEventListener('error', onError);
            windowRef.removeEventListener('unhandledrejection', onUnhandledRejection);
            if (windowRef.__psxRuntimeDiagnostics === state)
                delete windowRef.__psxRuntimeDiagnostics;
        }
    };
    windowRef.__psxRuntimeDiagnostics = state;
    return state.dispose;
}
