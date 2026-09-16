import { useMemo, useState, type JSX } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronRightIcon } from 'lucide-react';
import { MarkdownContent } from '../markdown/MarkdownContent.js';
import { DecisionOptionPills } from '../decisions/DecisionOptionPills.js';
import { Button } from '../components/ui/button.js';
import { useAnnounceLive } from '../ui/announce.js';
import { diffFileLines, type EditBlock } from './fileEditDiff.js';
import { resolveDisplay } from './copy.js';
import type { DecisionItem } from './timelineViewModel.js';
import type { DecisionCallbacks } from './TimelineDecisions.js';

function FilePreview({ block, historical, copyText, status, operation }: {
  block: EditBlock; historical: boolean; copyText: DecisionCallbacks['copyText']; status?: string; operation?: string;
}): JSX.Element {
  const { t } = useTranslation('agent');
  const live = useAnnounceLive();
  const [open, setOpen] = useState(!historical);
  const [limit, setLimit] = useState(300);
  const [copyFailed, setCopyFailed] = useState(false);
  const lines = useMemo(() => diffFileLines(block.oldText, block.newText), [block.oldText, block.newText]);
  return <div className="min-w-0">
    <div className="flex min-w-0 items-center gap-2 px-3 py-2">
      <button type="button" className="flex min-w-0 flex-1 items-center gap-2 text-left text-xs"
        title={block.path} aria-expanded={open} onClick={() => setOpen(value => !value)}>
        <ChevronRightIcon className={'size-3 shrink-0' + (open ? ' rotate-90' : '')} aria-hidden="true" />
        <span className="max-w-[40%] shrink-0 truncate text-muted-foreground" title={operation}>{operation || t('timeline.decision.fileEdit.edit')}</span>
        <span className="min-w-0 truncate font-mono font-normal">{block.displayPath}</span>
        {block.external && <span className="shrink-0 text-muted-foreground">{t('timeline.decision.fileEdit.external')}</span>}
      </button>
      {status && <span role="status" aria-live={live} className="shrink-0 text-xs text-muted-foreground">{status}</span>}
      <Button type="button" variant="ghost" size="sm" className="h-6 shrink-0 px-1 text-xs"
        title={block.path} onClick={async () => {
          try { setCopyFailed(!await copyText(block.path)); } catch { setCopyFailed(true); }
        }}>{t('timeline.decision.fileEdit.copyPath')}</Button>
    </div>
    {copyFailed && <p role="status" className="px-3 text-xs text-destructive">{t('timeline.decision.fileEdit.copyFailed')}</p>}
    {open && <>
      <div className="agent-native-scroll max-h-[360px] overflow-auto border-t text-xs" tabIndex={0}
        role="region" aria-label={block.displayPath}>
        <div className="w-max min-w-full font-mono leading-5">
          {lines.slice(0, limit).map((line, index) => <div key={index}
            data-diff-kind={line.kind}
            className={'flex whitespace-pre px-3' + (line.kind === 'add'
              ? ' bg-emerald-500/10' : line.kind === 'delete' ? ' bg-red-500/10' : '')}>
            <span aria-hidden="true" className="w-10 shrink-0 select-none text-right text-muted-foreground">{line.oldLine ?? ''}</span>
            <span aria-hidden="true" className="mr-3 w-10 shrink-0 select-none text-right text-muted-foreground">{line.newLine ?? ''}</span>
            <span className="w-4 shrink-0 select-none">{line.kind === 'add' ? '+' : line.kind === 'delete' ? '−' : ' '}</span>
            <span>{line.text}{line.noNewline && <span className="ml-3 text-muted-foreground">{t('timeline.decision.fileEdit.noNewline')}</span>}</span>
          </div>)}
          {lines.length === 0 && <p className="px-3 text-muted-foreground">{t('timeline.decision.fileEdit.empty')}</p>}
        </div>
      </div>
      {lines.length > limit && <Button type="button" variant="ghost" size="sm" className="m-1 text-xs"
        onClick={() => setLimit(value => value + 300)}>{t('timeline.decision.fileEdit.more', { count: lines.length - limit })}</Button>}
    </>}
  </div>;
}

export function FileEditDecision({ item, callbacks }: { item: DecisionItem; callbacks: DecisionCallbacks }): JSX.Element {
  const { t } = useTranslation('agent');
  const live = useAnnounceLive();
  const pending = item.decisionState === 'active';
  const selected = item.options.find(option => option.optionId === item.selectedOptionId);
  const rejected = selected?.kind === 'reject_once' || selected?.kind === 'reject_always';
  const accepted = selected?.kind === 'allow_once' || selected?.kind === 'allow_always';
  const status = pending ? t('timeline.decision.required')
    : item.headerState === 'selected' && rejected ? t('timeline.decision.fileEdit.rejected')
    : item.headerState === 'selected' && accepted ? t('timeline.decision.fileEdit.allowed')
    : resolveDisplay(item.statusCode, t) || item.statusText || t('timeline.decision.inactive');
  const blocks = item.editBlocks ?? [];
  const singleFile = blocks.length === 1 && blocks[0].type === 'diff';
  return <section className="agent-file-edit-decision mb-5 min-w-0 overflow-hidden rounded-lg border bg-card text-card-foreground"
    data-decision-state={pending ? 'active' : 'disabled'} data-request-id={item.requestId || undefined}
    data-tool-call-id={item.toolCallId || undefined}>
    {!singleFile && <header className="flex items-center justify-between gap-3 border-b px-3 py-2 text-xs">
      <span className="min-w-0 truncate" title={item.title}>{item.title || t('timeline.decision.fileEdit.files', { count: blocks.filter(block => block.type === 'diff').length })}</span>
      <span role="status" aria-live={live} className="text-muted-foreground">{status}</span>
    </header>}
    {blocks.map((block, index) => block.type === 'text'
      ? <MarkdownContent key={index} className="agent-message-body px-3 py-2" mode="static" surface="decision" source={block.text} />
      : <FilePreview key={index} block={block} historical={item.historical}
        copyText={callbacks.copyText} status={singleFile ? status : undefined} operation={singleFile ? item.title : undefined} />)}
    {item.rawText && item.rawText !== item.text && <details className="border-t text-xs">
      <summary className="cursor-pointer px-3 py-2 text-muted-foreground">{t('timeline.recovery.technicalDetails')}</summary>
      <pre className="agent-native-scroll m-0 max-h-60 overflow-auto whitespace-pre-wrap break-words px-3 pb-2">{item.rawText}</pre>
    </details>}
    {pending && <div className="border-t px-3 py-2">
      {item.options.length ? <DecisionOptionPills options={item.options}
        selectedOptionId={item.selectedOptionId} disabled={false} ariaLabel={t('timeline.decision.choosePermission')}
        onSelect={option => { if (item.decisionState === 'active') callbacks.onDecisionOption(item, option); }} />
        : <p role="status" className="text-xs text-destructive">{t('timeline.decision.noOptionsAcp')}</p>}
    </div>}
  </section>;
}
