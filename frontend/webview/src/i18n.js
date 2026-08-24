// i18n.js — the single shared i18next instance for the whole WebView.
//
// Native JS controllers (shell) and React islands (Agent chunk) import this
// one module, so there is exactly one instance per process. The shell
// namespace is bundled into the always-loaded main chunk and initialized
// synchronously BEFORE any controller renders, so startup never flashes a
// foreign language. The Agent and settings namespaces are added by their own
// lazy chunks (entry.ts / usageIsland) which statically bundle every shipped
// language for their scope — a language switch therefore applies in one
// atomic frame with no async resource loading.

import i18next from 'i18next';

import shellZhHans from './locales/zh-Hans/shell.json';
import shellEn from './locales/en/shell.json';

export const SUPPORTED_LOCALES = Object.freeze(['zh-Hans', 'zh-Hant', 'en', 'ja']);

const SHELL_RESOURCES = {
    'zh-Hans': { shell: shellZhHans },
    en: { shell: shellEn }
};

function localeFromBootstrapUrl() {
    try {
        const params = new window.URLSearchParams(window.location.search);
        const value = params.get('locale');
        return value !== null && SUPPORTED_LOCALES.includes(value) ? value : null;
    } catch {
        return null;
    }
}

export const i18n = i18next.createInstance({
    lng: localeFromBootstrapUrl() || 'zh-Hans',
    fallbackLng: 'en',
    // Resources are bundled inline: init completes synchronously and no
    // network request ever happens (offline packaging contract).
    resources: SHELL_RESOURCES,
    ns: ['shell'],
    defaultNS: 'shell',
    interpolation: { escapeValue: false }
});

void /** @type {unknown} */ (i18n.init());

export function applyDocumentLang() {
    // The Vitest harness imports this module under a bare Node environment
    // before installing its per-test jsdom window; only touch the DOM when
    // one exists.
    if (typeof document === 'undefined') return;
    document.documentElement.lang = i18n.resolvedLanguage || i18n.language;
}
applyDocumentLang();

/**
 * Translate through the shared instance.
 *
 * @param {string} key
 * @param {Record<string, unknown>=} options
 * @returns {string}
 */
export function t(key, options) {
    return i18n.t(key, options);
}

/** Subscribe to runtime language switches; returns an unsubscribe fn.
 *
 * @param {(locale: string) => void} callback
 * @returns {() => void} unsubscribe
 */
export function onLocaleChanged(callback) {
    const handler = () => callback(i18n.resolvedLanguage || i18n.language);
    i18n.on('languageChanged', handler);
    return () => i18n.off('languageChanged', handler);
}

/** Register a lazily loaded namespace's resources for every shipped
 * language of that boundary (called by entry.ts / usageIsland).
 *
 * @param {string} name
 * @param {Record<string, Record<string, unknown>>} resourcesByLocale
 */
export function registerNamespace(name, resourcesByLocale) {
    for (const [locale, resources] of Object.entries(resourcesByLocale)) {
        i18n.addResourceBundle(locale, name, resources, true, true);
    }
}
