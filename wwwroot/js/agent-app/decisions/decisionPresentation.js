// decisionPresentation.ts — shared semantic presentation helpers.
export function decisionOptionClass(option) {
    const kind = String(option?.kind || '').toLowerCase();
    if (kind === 'allow_once')
        return 'agent-btn-allow';
    if (kind === 'allow_always')
        return 'agent-btn-always-allow';
    if (kind === 'reject_once' || kind === 'reject_always')
        return 'agent-btn-reject';
    const optionId = String(option?.optionId || '').toLowerCase();
    if (optionId === 'allow' || optionId === 'yes')
        return 'agent-btn-allow';
    if (optionId === 'always_allow')
        return 'agent-btn-always-allow';
    if (optionId === 'reject' || optionId === 'no')
        return 'agent-btn-reject';
    return '';
}
