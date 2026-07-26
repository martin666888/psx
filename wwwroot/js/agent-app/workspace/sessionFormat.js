// sessionFormat.ts — pure session/context formatting shared by the legacy
// renderer (SessionRuntimeController) and the React session island, so both
// paths produce byte-identical text from the same reduced state.
/** Mirrors legacy _formatStatus. */
export function formatStatus(status) {
    return String(status || 'ready')
        .split('_')
        .filter(Boolean)
        .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
        .join(' ');
}
export function formatTokens(value) {
    return new Intl.NumberFormat('en-US', {
        notation: 'compact',
        maximumFractionDigits: value >= 100_000 ? 0 : 1
    }).format(value);
}
export function formatPercent(value) {
    return (value < 10 ? value.toFixed(1) : Math.round(value).toString()) + '%';
}
export function formatCost(amount, currency) {
    return amount.toFixed(2).replace(/\.00$/, '') + ' ' + currency;
}
export function computeContextUsageView(used, size, costAmount, costCurrency) {
    const hasLimit = used !== null && size !== null;
    const percent = hasLimit ? Math.min(100, Math.max(0, (used / size) * 100)) : 0;
    const state = !hasLimit ? 'unknown' : percent >= 90 ? 'error' : percent >= 75 ? 'warning' : 'accent';
    let summary = 'Agent has not reported context usage';
    let detail = 'Context usage will appear when the Agent reports it.';
    if (hasLimit) {
        summary = formatPercent(percent) + ' · ' + formatTokens(used) + ' / ' + formatTokens(size);
        detail = formatTokens(Math.max(0, size - used)) + ' remaining';
    }
    else if (used !== null) {
        summary = formatTokens(used) + ' used';
        detail = 'Agent did not report a context limit.';
    }
    else if (size === null) {
        detail = 'Agent did not report a context limit.';
    }
    const hasCost = costAmount !== null && !!costCurrency;
    return {
        state,
        percent,
        summary,
        detail,
        ariaLabel: 'Context: ' + summary + '. ' + detail,
        cost: hasCost ? 'Cost · ' + formatCost(costAmount, costCurrency) : ''
    };
}
