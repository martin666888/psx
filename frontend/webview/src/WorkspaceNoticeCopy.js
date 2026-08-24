// WorkspaceNoticeCopy.js — fixed workspace_notice codes → display sentences.
// C# sends only the stable code (Models/WorkspaceNoticeCode.cs); the copy
// lives in the shell locales under notice.*, so a language switch applies
// without any producer change.

import { t } from './i18n.js';

const GENERIC_NOTICE_KEY = 'notice.generic';

export function workspaceNoticeLabel(code) {
    if (typeof code !== 'string' || !code) return '';
    return t(`notice.${code}`, { defaultValue: '' }) || t(GENERIC_NOTICE_KEY);
}
