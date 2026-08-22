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
  useCallback,
  useEffect,
  useRef,
  type JSX,
  type KeyboardEvent,
  type PointerEvent
} from 'react';
import { RefreshCwIcon } from 'lucide-react';
import { Button } from '../components/ui/button.js';
import { Input } from '../components/ui/input.js';
import { ScrollArea } from '../components/ui/scroll-area.js';
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
  const contentRef = useRef<HTMLDivElement | null>(null);
  const resizerRef = useRef<HTMLDivElement | null>(null);
  // Pointer-capture drag state: recorded on pointerdown, cleared on release.
  const dragRef = useRef<{
    pointerId: number;
    startClientX: number;
    startWidth: number;
    latestWidth: number;
  } | null>(null);
  // Pointer events can outpace the display refresh. Keep only the newest
  // preview and perform at most one shared-layout write per rendered frame.
  const previewFrameRef = useRef<number | null>(null);
  const pendingWidthRef = useRef<number | null>(null);

  /** Every drag exit funnels through one idempotent settle path. Pointer
   * capture can be lost without a final pointerup (for example when WebView2
   * loses window focus), so the latest preview width must still become the
   * committed width and release PaneLayout's interactive-resize mode. */
  const finishResize = useCallback((
    event?: PointerEvent<HTMLDivElement>,
    useLatestWidth = false
  ): void => {
    const drag = dragRef.current;
    if (!drag) return;
    dragRef.current = null;
    if (previewFrameRef.current !== null) cancelAnimationFrame(previewFrameRef.current);
    previewFrameRef.current = null;
    pendingWidthRef.current = null;
    document.body.classList.remove('agent-history-dock-resizing');
    try {
      if (resizerRef.current?.hasPointerCapture(drag.pointerId)) {
        resizerRef.current.releasePointerCapture(drag.pointerId);
      }
    } catch {
      // Capture may already be gone when lostpointercapture/window blur wins.
    }
    const commitWidth = useLatestWidth || !event
      ? drag.latestWidth
      : clampWidth(drag.startWidth + (event.clientX - drag.startClientX));
    resizerRef.current?.setAttribute('aria-valuenow', String(commitWidth));
    props.onWidthCommit(commitWidth);
  }, [props.onWidthCommit]);

  // React updates the list in place, so the scroll position survives
  // store-driven renders naturally; only search/filter changes bump the
  // token and zero the viewport.
  useEffect(() => {
    if (contentRef.current) contentRef.current.scrollTop = 0;
  }, [props.resetScrollToken]);

  useEffect(() => {
    const onWindowBlur = (): void => finishResize(undefined, true);
    window.addEventListener('blur', onWindowBlur);
    return () => window.removeEventListener('blur', onWindowBlur);
  }, [finishResize]);

  useEffect(() => () => {
    const drag = dragRef.current;
    dragRef.current = null;
    if (previewFrameRef.current !== null) cancelAnimationFrame(previewFrameRef.current);
    previewFrameRef.current = null;
    pendingWidthRef.current = null;
    document.body.classList.remove('agent-history-dock-resizing');
    try {
      if (drag && resizerRef.current?.hasPointerCapture(drag.pointerId)) {
        resizerRef.current.releasePointerCapture(drag.pointerId);
      }
    } catch {
      // Unmount cleanup is best-effort; the element may already be detached.
    }
  }, []);

  const applyPendingPreview = (): void => {
    previewFrameRef.current = null;
    const nextWidth = pendingWidthRef.current;
    pendingWidthRef.current = null;
    if (nextWidth === null || !dragRef.current) return;
    // Avoid a React render for per-frame ARIA feedback; the committed width
    // returns through props after pointerup/keyboard resize.
    resizerRef.current?.setAttribute('aria-valuenow', String(nextWidth));
    props.onWidthPreview(nextWidth);
  };

  const onResizerPointerDown = (event: PointerEvent<HTMLDivElement>): void => {
    if (event.button !== 0) return;
    const startWidth = clampWidth(props.width);
    dragRef.current = {
      pointerId: event.pointerId,
      startClientX: event.clientX,
      startWidth,
      latestWidth: startWidth
    };
    resizerRef.current?.setPointerCapture(event.pointerId);
    document.body.classList.add('agent-history-dock-resizing');
    event.preventDefault();
  };

  const onResizerPointerMove = (event: PointerEvent<HTMLDivElement>): void => {
    const drag = dragRef.current;
    if (!drag) return;
    // Reapply the pointer delta to the immutable drag-start width. Reading
    // live geometry here would force layout after the previous frame's write.
    const nextWidth = clampWidth(drag.startWidth + (event.clientX - drag.startClientX));
    drag.latestWidth = nextWidth;
    pendingWidthRef.current = nextWidth;
    if (previewFrameRef.current === null) {
      previewFrameRef.current = requestAnimationFrame(applyPendingPreview);
    }
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
      className="agent-history-dock"
      data-role="history-dock"
      data-agent-font-surface=""
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
      {/* Radix ScrollArea (hover-reveal thumb). The viewport keeps the
        * agent-history-dock-content class, data-role anchor and scroll ref so
        * the rounded-corner CSS, tests and the scroll-reset contract keep
        * addressing the real scroll node. */}
      <ScrollArea
        className="min-h-0 flex-1"
        type="hover"
        viewportProps={{
          ref: contentRef,
          className: 'agent-history-dock-content p-2',
          'data-role': 'history-content'
        }}
      >
        <HistoryList {...props.list} />
      </ScrollArea>
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
        aria-valuenow={clampWidth(props.width)}
        tabIndex={0}
        onPointerDown={onResizerPointerDown}
        onPointerMove={onResizerPointerMove}
        onPointerUp={(event) => finishResize(event)}
        onPointerCancel={(event) => finishResize(event, true)}
        onLostPointerCapture={(event) => finishResize(event, true)}
        onKeyDown={onResizerKeyDown}
      />
    </aside>
  );
}
