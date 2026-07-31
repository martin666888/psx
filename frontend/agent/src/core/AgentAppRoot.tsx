// AgentAppRoot.tsx — the single React root for the whole Agent app (CP3a).
//
// All migrated regions render inside ONE React tree: island controllers
// register { host, element } entries in an external registry store, the root
// view subscribes through useSyncExternalStore and renders one portal per
// entry into that region's stable host element. This replaces the previous
// one-createRoot-per-island infrastructure while keeping the exact
// controller-facing contract (mountReactIsland / IslandHandle) intact.
//
// keep-alive contract: an entry lives until its controller disposes it —
// hiding a workspace panel hides the host element but never unmounts the
// portal, so component state survives tab switches; store updates keep
// flowing to hidden workspaces because rendering never depends on
// visibility. Every region keeps its own error boundary (IslandFrame), so a
// crash inside History/Timeline/Composer/Workspace regions stays contained
// to that region.

import {
  Component,
  memo,
  useLayoutEffect,
  useSyncExternalStore,
  type ErrorInfo,
  type JSX,
  type ReactNode
} from 'react';
import { createPortal, flushSync } from 'react-dom';
import { createRoot, type Root } from 'react-dom/client';
import type { IslandFailureReporter } from './islandHost.js';

interface IslandEntry {
  key: number;
  name: string;
  host: HTMLElement;
  element: ReactNode;
  reportFailure: IslandFailureReporter;
}

// --- external registry store (useSyncExternalStore contract) ---------------

let nextKey = 1;
const entries = new Map<number, IslandEntry>();
let snapshot: readonly IslandEntry[] = [];
const listeners = new Set<() => void>();

function commitSnapshot(): void {
  snapshot = [...entries.values()];
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function getSnapshot(): readonly IslandEntry[] {
  return snapshot;
}

function notify(): void {
  for (const listener of [...listeners]) listener();
}

// --- per-entry frame (error boundary + commit marker + portal) -------------

interface FrameProps {
  entry: IslandEntry;
}

interface BoundaryState {
  failedFor: ReactNode | null;
}

class IslandErrorBoundary extends Component<FrameProps, BoundaryState> {
  override state: BoundaryState = { failedFor: null };

  static getDerivedStateFromError(): Partial<BoundaryState> {
    return {};
  }

  override componentDidCatch(error: Error, _info: ErrorInfo): void {
    // Remember which element crashed: the controller reacts to reportFailure
    // by disposing this entry and rendering its own failure card, and a retry
    // registers a NEW element, which re-arms the boundary below.
    this.setState({ failedFor: this.props.entry.element });
    this.props.entry.reportFailure(error, 'render');
  }

  override render(): ReactNode {
    if (this.state.failedFor !== null) {
      if (this.state.failedFor === this.props.entry.element) return null;
      // A new element arrived after the crash — re-arm and try again.
      queueMicrotask(() => this.setState({ failedFor: null }));
      return null;
    }
    return createPortal(
      <>
        <CommitMarker host={this.props.entry.host} />
        {this.props.entry.element}
      </>,
      this.props.entry.host
    );
  }
}

function CommitMarker({ host }: { host: HTMLElement }): null {
  useLayoutEffect(() => {
    host.dataset.islandState = 'mounted';
    host.removeAttribute('aria-busy');
  });
  return null;
}

// memo: a streaming Timeline update must not re-render every other region —
// only entries whose object identity changed (setElement replaces the entry)
// re-render.
const IslandPortal = memo(function IslandPortal({ entry }: FrameProps): JSX.Element {
  return <IslandErrorBoundary entry={entry} />;
});

function AgentIslandsView(): JSX.Element {
  const current = useSyncExternalStore(subscribe, getSnapshot);
  return (
    <>
      {current.map((entry) => (
        <IslandPortal entry={entry} key={entry.key} />
      ))}
    </>
  );
}

// --- the single root --------------------------------------------------------

let root: Root | null = null;
let rootContainer: HTMLElement | null = null;

function disposeRootWhenEmpty(): void {
  if (entries.size !== 0 || !root) return;
  const currentRoot = root;
  const currentContainer = rootContainer;
  // Clear the module-level handles before unmounting so cleanup code cannot
  // accidentally reuse a root that is already being torn down.
  root = null;
  rootContainer = null;
  currentRoot.unmount();
  listeners.clear();
  currentContainer?.remove();
}

function ensureRoot(): void {
  // Tests swap globalThis.document per file (agentHarness installs a fresh
  // jsdom); a root bound to a dead document must be rebuilt.
  if (root && rootContainer && rootContainer.ownerDocument === document) return;
  if (root) {
    // Defensive fallback for a caller that replaced the Document without
    // disposing every island first. Normal app/test teardown removes the last
    // island while its Document is still alive, and disposeRootWhenEmpty()
    // unmounts the root synchronously before the window closes.
    root = null;
    rootContainer = null;
    entries.clear();
    commitSnapshot();
    listeners.clear();
  }
  rootContainer = document.createElement('div');
  rootContainer.dataset.role = 'agent-react-root';
  rootContainer.style.display = 'contents';
  document.body.appendChild(rootContainer);
  root = createRoot(rootContainer, {
    onUncaughtError(error): void {
      console.error('[agent] Uncaught error escaped the Agent React root.', error);
    },
    onRecoverableError(error): void {
      console.warn('[agent] Agent React root recovered from an error.', error);
    }
  });
  root.render(<AgentIslandsView />);
}

// --- registry API used by mountReactIsland ---------------------------------

export interface RegisteredIsland {
  setElement(element: ReactNode): void;
  remove(): void;
}

export function registerIsland(
  name: string,
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): RegisteredIsland {
  ensureRoot();
  const key = nextKey++;
  let removed = false;
  return {
    setElement(element: ReactNode): void {
      if (removed) return;
      entries.set(key, { key, name, host, element, reportFailure });
      commitSnapshot();
      notify();
    },
    remove(): void {
      if (removed) return;
      removed = true;
      if (!entries.delete(key)) return;
      commitSnapshot();
      // Synchronous commit: the island host clears its DOM right after
      // dispose(), so the portal must be detached before that happens (the
      // per-root implementation had the same synchronous unmount semantics).
      flushSync(() => notify());
      // A shared root with no islands has no keep-alive value. Unmount it while
      // its owning Document is still valid so React effects, scheduler tasks
      // and DOM references cannot leak into the next app/test runtime.
      disposeRootWhenEmpty();
    }
  };
}
