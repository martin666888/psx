// HistoryDockView.tsx — React-owned chrome for the global History dock.
//
// Renders the whole dock frame inside the controller's portal host: the
// <aside> shell, the search / provider-filter / refresh top bar (shadcn
// Input, Select, Button), the single scroll node around HistoryList, and the
// edge resizer with its pointer-capture drag and keyboard resizing.
// HistoryDockController keeps the business logic — the open-state machine,
// width clamp/persistence/broadcast and the toolbar-toggle aria — and the two
// sides meet at the HistoryDockProps contract. Every semantic anchor
// (agent-history-* classes, data-role attributes, separator aria triple)
// survives unchanged for tests and replay.

import {
  useEffect,
  useRef,
  useState,
  type JSX,
  type KeyboardEvent,
  type PointerEvent
} from 'react';
import { RefreshCwIcon } from 'lucide-react';
import { Button } from '../components/ui/button.js';
import { Input } from '../components/ui/input.js';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue
} from '../components/ui/select.js';
import { HISTORY_DOCK_MAX_WIDTH, HISTORY_DOCK_MIN_WIDTH } from './historyModel.js';
import { HistoryList, type HistoryListProps } from './HistoryList.js';

export interface HistoryDockProps {
  open: boolean;
  query: string;
  providerFilter: string;
  providers: { key: string; label: string }[];
  width: number;
  onQueryChange(q: string): void;
  onProviderChange(key: string): void;
  onRefresh(): void;
  /** Live drag feedback: applies the width without persisting or broadcasting. */
  onWidthPreview(width: number): void;
  /** Completed resize: the controller clamps, persists and broadcasts. */
  onWidthCommit(width: number): void;
  /** Monotonic; whenever it changes the list viewport scrolls back to top. */
  resetScrollToken: number;
}

export interface HistoryDockViewProps extends HistoryDockProps {
  list: HistoryListProps;
}

/** Radix Select rejects empty item values, so the "All providers" entry maps
 * the controller's '' filter onto this sentinel at the UI boundary. */
const ALL_PROVIDERS = '__all__';

const KEYBOARD_STEP = 16;
const KEYBOARD_STEP_FAST = 40;

function clampWidth(width: number): number {
  return Math.max(HISTORY_DOCK_MIN_WIDTH, Math.min(HISTORY_DOCK_MAX_WIDTH, Math.round(width)));
}

export function HistoryDockView(props: HistoryDockViewProps): JSX.Element {
  const dockRef = useRef<HTMLElement | null>(null);
  const contentRef = useRef<HTMLDivElement | null>(null);
  const resizerRef = useRef<HTMLDivElement | null>(null);
  // Pointer-capture drag state: recorded on pointerdown, cleared on release.
  const dragRef = useRef<{ startClientX: number; startWidth: number } | null>(null);
  // Live aria feedback while a drag is in flight; null between commits.
  const [previewWidth, setPreviewWidth] = useState<number | null>(null);

  // React updates the list in place, so the scroll position survives
  // store-driven renders naturally; only search/filter changes bump the
  // token and zero the viewport.
  useEffect(() => {
    if (contentRef.current) contentRef.current.scrollTop = 0;
  }, [props.resetScrollToken]);

  const onResizerPointerDown = (event: PointerEvent<HTMLDivElement>): void => {
    if (event.button !== 0) return;
    dragRef.current = { startClientX: event.clientX, startWidth: props.width };
    resizerRef.current?.setPointerCapture(event.pointerId);
    document.body.classList.add('agent-history-dock-resizing');
    event.preventDefault();
  };

  const onResizerPointerMove = (event: PointerEvent<HTMLDivElement>): void => {
    const drag = dragRef.current;
    const dock = dockRef.current;
    if (!drag || !dock) return;
    // The dock's left edge is fixed for the whole drag, so the pointer's
    // viewport position is the next width directly.
    const nextWidth = event.clientX - dock.getBoundingClientRect().left;
    setPreviewWidth(nextWidth);
    props.onWidthPreview(nextWidth);
  };

  const endResize = (event: PointerEvent<HTMLDivElement>): void => {
    const drag = dragRef.current;
    if (!drag) return;
    dragRef.current = null;
    setPreviewWidth(null);
    document.body.classList.remove('agent-history-dock-resizing');
    try {
      resizerRef.current?.releasePointerCapture(event.pointerId);
    } catch {
      // Pointer capture may already be gone if the window lost focus.
    }
    const dockLeft = dockRef.current?.getBoundingClientRect().left;
    const commitWidth =
      dockLeft === undefined
        ? drag.startWidth + (event.clientX - drag.startClientX)
        : event.clientX - dockLeft;
    props.onWidthCommit(commitWidth);
  };

  const onResizerKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    const step = event.shiftKey ? KEYBOARD_STEP_FAST : KEYBOARD_STEP;
    let nextWidth: number;
    if (event.key === 'ArrowLeft') nextWidth = props.width - step;
    else if (event.key === 'ArrowRight') nextWidth = props.width + step;
    else if (event.key === 'Home') nextWidth = HISTORY_DOCK_MAX_WIDTH;
    else if (event.key === 'End') nextWidth = HISTORY_DOCK_MIN_WIDTH;
    else return;
    event.preventDefault();
    props.onWidthCommit(nextWidth);
  };

  return (
    <aside
      ref={dockRef}
      className="agent-history-dock"
      data-role="history-dock"
      aria-label="Agent history"
      hidden={!props.open}
    >
      <div className="agent-history-dock-bar">
        <Input
          type="search"
          className="agent-history-search h-8 text-xs [grid-column:1/-1]"
          data-role="history-search"
          placeholder="Search threads"
          aria-label="Search history"
          value={props.query}
          onChange={(event) => props.onQueryChange(event.target.value)}
        />
        <Select
          value={props.providerFilter || ALL_PROVIDERS}
          onValueChange={(key) => props.onProviderChange(key === ALL_PROVIDERS ? '' : key)}
        >
          <SelectTrigger
            className="agent-history-provider-filter w-full min-w-0 text-xs"
            size="sm"
            data-role="history-provider-filter"
            aria-label="Filter by provider"
          >
            <SelectValue placeholder="All providers" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL_PROVIDERS}>All providers</SelectItem>
            {props.providers.map((provider) => (
              <SelectItem key={provider.key} value={provider.key}>
                {provider.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button
          variant="outline"
          size="icon"
          className="agent-history-refresh size-8 shrink-0 border-input"
          data-role="history-refresh"
          title="Refresh history"
          aria-label="Refresh history"
          onClick={props.onRefresh}
        >
          <RefreshCwIcon className="size-3.5" aria-hidden="true" />
        </Button>
      </div>
      <div
        ref={contentRef}
        className="agent-history-dock-content min-h-0 flex-1 overflow-x-hidden overflow-y-auto p-2"
        data-role="history-content"
      >
        <HistoryList {...props.list} />
      </div>
      <div
        ref={resizerRef}
        className="agent-history-dock-resizer"
        data-role="history-dock-resizer"
        role="separator"
        aria-label="Resize Agent history"
        aria-orientation="vertical"
        // role=separator requires the value triple when focusable (axe
        // aria-required-attr); the controller clamps every committed width.
        aria-valuemin={HISTORY_DOCK_MIN_WIDTH}
        aria-valuemax={HISTORY_DOCK_MAX_WIDTH}
        aria-valuenow={clampWidth(previewWidth ?? props.width)}
        tabIndex={0}
        onPointerDown={onResizerPointerDown}
        onPointerMove={onResizerPointerMove}
        onPointerUp={endResize}
        onPointerCancel={endResize}
        onKeyDown={onResizerKeyDown}
      />
    </aside>
  );
}
