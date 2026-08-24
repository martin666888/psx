// DshRegistryUi.js — DSH download-source labels and failure CTAs. Typed error classes come from C# `DshErrorClass`. Official is
// upstream: official E404 never suggests the mirror. The copy tables map the
// fixed wire codes (`errorClass` / `updateErrorCode`) to display sentences;
// the backend never sends composed text.

import { t } from './i18n.js';

export const DSH_REGISTRY_KEYS = Object.freeze(['official', 'npmmirror']);

export const DSH_REGISTRY_LABELS = Object.freeze({
    official: 'npmjs.org',
    npmmirror: 'npmmirror.com'
});

export const DSH_REGISTRY_NOTES = Object.freeze({
    official: '官方',
    npmmirror: '淘宝镜像'
});

// The runtime/update error copy tables moved into the shell locales
// (dsh.runtimeErrorCopy / dsh.updateErrorCopy); lookups go through the
// shared i18n instance so a language switch re-renders in place.

export function dshUpdateErrorLabel(code) {
    if (typeof code !== 'string' || !code) return '';
    return t(`dsh.updateErrorCopy.${code}`) || t('dsh.updateErrorCopy.update_failed');
}

export function safeDshRegistry(value) {
    return value === 'npmmirror' ? 'npmmirror' : 'official';
}

export function otherDshRegistry(key) {
    return safeDshRegistry(key) === 'npmmirror' ? 'official' : 'npmmirror';
}

export function dshSourceLabel(status) {
    const operationKey = status?.operationRegistryKey;
    if (operationKey === 'official' || operationKey === 'npmmirror')
        return `本次来源：${DSH_REGISTRY_LABELS[operationKey]}`;
    return `当前默认下载源：${DSH_REGISTRY_LABELS[safeDshRegistry(status?.registryKey)]}`;
}

/**
 * @returns {{ command: string, registry: string, label: string } | null}
 */
export function dshSwitchCta(status) {
    const typed = status?.runtimeErrorClass || status?.updateErrorClass;
    if (!typed) return null;
    if (typed === 'settings_write_failed'
        || typed === 'lock_unavailable'
        || typed === 'catalog_corrupt'
        || typed === 'install_failed'
        || typed === 'launch_failed')
        return null;

    const current = safeDshRegistry(status?.operationRegistryKey || status?.registryKey);
    const installed = Boolean(status?.currentVersion) && status?.state !== 'not_installed';
    const command = installed ? 'recheck_with_registry' : 'retry_install_with_registry';
    const verb = installed ? '重新检查' : '重新安装';

    if (typed === 'registry_timeout' || typed === 'registry_network') {
        const registry = otherDshRegistry(current);
        return {
            command,
            registry,
            label: `改用 ${DSH_REGISTRY_LABELS[registry]} ${verb}`
        };
    }

    if (typed === 'registry_not_found' || typed === 'integrity_failed') {
        if (current !== 'npmmirror') return null;
        return {
            command,
            registry: 'official',
            label: `改用 ${DSH_REGISTRY_LABELS.official} ${verb}`
        };
    }

    return null;
}
