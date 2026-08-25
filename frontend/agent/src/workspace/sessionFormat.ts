// sessionFormat.ts — pure session/context formatting used by the React session
// island. PSX-authored wording stays as fixed locale keys + interpolation
// params; the React view resolves them at render so language switches apply.

/** Mirrors legacy _formatStatus: a status-token prettifier for the wire enum.
 *  The result is a capitalized token ('auth required'), never a sentence. */
export function formatStatus(status: string): string {
  return String(status || 'ready')
    .split('_')
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

export function formatTokens(value: number): string {
  return new Intl.NumberFormat('en-US', {
    notation: 'compact',
    maximumFractionDigits: value >= 100_000 ? 0 : 1
  }).format(value);
}

export function formatPercent(value: number): string {
  return (value < 10 ? value.toFixed(1) : Math.round(value).toString()) + '%';
}

export function formatCost(amount: number, currency: string): string {
  return amount.toFixed(2).replace(/\.00$/, '') + ' ' + currency;
}

/** Everything the Context ring renders, derived from the session slice.
 *  `summaryKey`/`detailKey`/`costKey` are agent-locale keys; params carry
 *  preformatted numbers (locale-neutral data). The React view resolves them
 *  with useTranslation at render. */
export interface ContextUsageView {
  state: 'unknown' | 'accent' | 'warning' | 'error';
  percent: number;
  summaryKey: string;
  summaryParams: Record<string, unknown>;
  detailKey: string;
  detailParams: Record<string, unknown>;
  /** '' hides the cost row. */
  costKey: string;
  costParams: Record<string, unknown>;
}

const NO_USAGE_SUMMARY = 'session.context.noUsageSummary';
const NO_USAGE_DETAIL = 'session.context.noUsageDetail';
const NO_LIMIT_DETAIL = 'session.context.noLimitDetail';

export function computeContextUsageView(
  used: number | null,
  size: number | null,
  costAmount: number | null,
  costCurrency: string
): ContextUsageView {
  const hasLimit = used !== null && size !== null;
  const percent = hasLimit ? Math.min(100, Math.max(0, (used / size) * 100)) : 0;
  const state = !hasLimit ? 'unknown' : percent >= 90 ? 'error' : percent >= 75 ? 'warning' : 'accent';

  let summaryKey = NO_USAGE_SUMMARY;
  let summaryParams: Record<string, unknown> = {};
  let detailKey = NO_USAGE_DETAIL;
  let detailParams: Record<string, unknown> = {};
  if (hasLimit) {
    summaryKey = 'session.context.limitedSummary';
    summaryParams = {
      percent: formatPercent(percent),
      used: formatTokens(used as number),
      size: formatTokens(size as number)
    };
    detailKey = 'session.context.remainingDetail';
    detailParams = { remaining: formatTokens(Math.max(0, (size as number) - (used as number))) };
  } else if (used !== null) {
    summaryKey = 'session.context.usedSummary';
    summaryParams = { used: formatTokens(used) };
    detailKey = NO_LIMIT_DETAIL;
  } else if (size === null) {
    detailKey = NO_LIMIT_DETAIL;
  }

  const hasCost = costAmount !== null && !!costCurrency;
  return {
    state,
    percent,
    summaryKey,
    summaryParams,
    detailKey,
    detailParams,
    costKey: hasCost ? 'session.context.costLine' : '',
    costParams: hasCost
      ? { amount: formatCost(costAmount as number, costCurrency) }
      : {}
  };
}
