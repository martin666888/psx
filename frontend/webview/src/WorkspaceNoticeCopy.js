// WorkspaceNoticeCopy.js — fixed workspace_notice codes → display sentences.
// C# sends only the stable code (Models/WorkspaceNoticeCode.cs); composed
// sentences never cross the bridge.

const WORKSPACE_NOTICE_COPY = Object.freeze({
    'workspace.worktree_conflict': '多个可见 Agent 正在使用同一 worktree，请留意并发修改冲突。',
    'dsh.export.invalid_response': '导出失败：服务返回了无效响应。',
    'dsh.export.invalid_content_type': '导出失败：会话数据格式无效。',
    'dsh.export.invalid_data': '导出失败：会话数据无效。',
    'dsh.export.too_large': '导出失败：文件过大，已取消。',
    'dsh.export.write_failed': '导出失败：文件写入没有完成。',
    'dsh.export.connect_failed': '导出失败：无法连接本地服务。',
    'kimi.export.not_ready': '导出失败：本地服务未就绪，请稍后重试。',
    'kimi.export.invalid_request': '导出失败：导出请求无效。',
    'kimi.export.invalid_response': '导出失败：服务返回了无效响应。',
    'kimi.export.invalid_content_type': '导出失败：会话数据格式无效。',
    'kimi.export.invalid_data': '导出失败：会话数据无效。',
    'kimi.export.too_large': '导出失败：文件过大，已取消。',
    'kimi.export.write_failed': '导出失败：文件写入没有完成。',
    'kimi.export.connect_failed': '导出失败：无法连接本地服务。'
});

const GENERIC_NOTICE_COPY = '操作没有完成，请重试。';

export function workspaceNoticeLabel(code) {
    if (typeof code !== 'string' || !code) return '';
    return WORKSPACE_NOTICE_COPY[code] || GENERIC_NOTICE_COPY;
}
