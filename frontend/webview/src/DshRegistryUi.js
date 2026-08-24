// DshRegistryUi.js — DSH download-source labels, error-code copy tables and
// failure CTAs. Typed error classes come from C# `DshErrorClass`. Official is
// upstream: official E404 never suggests the mirror. The copy tables map the
// fixed wire codes (`errorClass` / `updateErrorCode`) to display sentences;
// the backend never sends composed text.

export const DSH_REGISTRY_KEYS = Object.freeze(['official', 'npmmirror']);

export const DSH_REGISTRY_LABELS = Object.freeze({
    official: 'npmjs.org',
    npmmirror: 'npmmirror.com'
});

export const DSH_REGISTRY_NOTES = Object.freeze({
    official: '官方',
    npmmirror: '淘宝镜像'
});

// dsh_runtime_status.errorClass → runtime/install failure sentence.
export const DSH_RUNTIME_ERROR_COPY = Object.freeze({
    runtime_unavailable: '运行时不可用',
    process_exited: 'DSH 进程意外退出。',
    start_timeout: 'DSH 启动超时（90 秒内未就绪）。',
    launch_failed: '无法启动 DSH 进程。',
    install_failed: '安装失败：npm ci 没有成功完成。',
    install_cancelled: '安装已停止。',
    portable_node_missing: '缺少便携 Node.js 运行时，无法安装。',
    seed_missing: '缺少 DSH 安装种子文件，无法安装。',
    entry_missing: '安装后入口文件缺失 (lib/bin.js)。',
    version_unreadable: '安装后无法读取 DSH 版本。',
    version_mismatch: '安装结果与锁定版本不一致，已拒绝启用。',
    install_switch_failed: '安装目录切换失败，运行时未启用。',
    post_install_unavailable: '安装后运行时不可用。',
    registry_network: '网络不可用，无法连接 npm 仓库。请检查网络后重试。',
    registry_not_found: '仓库中找不到锁定版本。',
    integrity_failed: '安装包未通过完整性校验。',
    settings_write_failed: '无法保存下载源设置，未开始重试。',
    catalog_corrupt: 'PSX 安装文件损坏，请重新安装。'
});

// dsh_runtime_status.updateErrorCode → update/check failure sentence.
export const DSH_UPDATE_ERROR_COPY = Object.freeze({
    update_requires_psx: '请先更新 PSX。',
    update_registry_changed: '更新源已变更，请重新检查更新。',
    update_invalid_version: '更新版本无效，请重新检查更新。',
    update_cancelled: '更新已停止，当前版本未改变。',
    update_failed: '更新失败，当前版本可继续使用。',
    update_switch_failed: '更新切换失败，已保留原版本。',
    update_relaunch_failed: '新版本启动失败，已恢复原版本。',
    not_installed: '请先安装 DeepSeek Harness。',
    update_node_missing: '缺少便携 Node.js，无法检查更新。',
    invalid_versions: 'npm 仓库返回了无效版本，未执行更新。',
    candidate_invalid: '待更新版本无效或不高于当前版本。',
    registry_config_missing: '缺少 DSH Registry 配置，无法安全更新。',
    update_workspace_failed: '无法准备 DSH 更新目录。',
    receipt_write_failed: '无法记录更新来源，当前版本未改变。',
    update_marker_failed: '更新已下载，但无法写入激活标记。',
    check_failed: '检查更新失败，当前版本可继续使用。',
    registry_network: '无法连接 npm 仓库，请检查网络后重试。',
    registry_not_found: '仓库中找不到可用版本。',
    integrity_failed: '下载的更新未通过完整性校验，当前版本未改变。',
    lock_unavailable: '该版本暂不可安装，请更新 PSX 后重试。',
    catalog_corrupt: 'PSX 安装文件损坏，请重新安装。',
    settings_write_failed: '无法保存下载源设置，未开始重试。'
});

export function dshUpdateErrorLabel(code) {
    if (typeof code !== 'string' || !code) return '';
    return DSH_UPDATE_ERROR_COPY[code] || DSH_UPDATE_ERROR_COPY.update_failed;
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
