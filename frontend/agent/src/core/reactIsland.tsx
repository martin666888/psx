// reactIsland.tsx — shared mounting tool for independent React roots.
//
// Every call creates its own root. Sharing this helper standardizes commit
// diagnostics and failure containment without coupling island lifecycles.

import {
  Component,
  useLayoutEffect,
  type ErrorInfo,
  type JSX,
  type ReactNode
} from 'react';
import { createRoot, type Root } from 'react-dom/client';
import type {
  IslandFailureReporter,
  IslandHandle
} from './islandHost.js';

interface BoundaryProps {
  children: ReactNode;
  host: HTMLElement;
  reportFailure: IslandFailureReporter;
}

interface BoundaryState {
  failed: boolean;
}

class IslandErrorBoundary extends Component<BoundaryProps, BoundaryState> {
  override state: BoundaryState = { failed: false };

  static getDerivedStateFromError(): BoundaryState {
    return { failed: true };
  }

  override componentDidCatch(error: Error, _info: ErrorInfo): void {
    this.props.reportFailure(error, 'render');
  }

  override render(): ReactNode {
    if (this.state.failed) return null;
    return this.props.children;
  }
}

function CommitMarker({ host }: { host: HTMLElement }): null {
  useLayoutEffect(() => {
    host.dataset.islandState = 'mounted';
    host.removeAttribute('aria-busy');
  });
  return null;
}

function IslandFrame(props: BoundaryProps): JSX.Element {
  return (
    <IslandErrorBoundary host={props.host} reportFailure={props.reportFailure}>
      <CommitMarker host={props.host} />
      {props.children}
    </IslandErrorBoundary>
  );
}

export function mountReactIsland<TProps>(
  name: string,
  host: HTMLElement,
  reportFailure: IslandFailureReporter,
  renderElement: (props: TProps) => ReactNode
): IslandHandle<TProps> {
  let disposed = false;
  const root: Root = createRoot(host, {
    onUncaughtError(error): void {
      reportFailure(error, 'render');
    },
    onRecoverableError(error): void {
      console.warn('[agent] React island "' + name + '" recovered from an error.', error);
    }
  });

  return {
    render(props: TProps): void {
      if (disposed) return;
      root.render(
        <IslandFrame host={host} reportFailure={reportFailure}>
          {renderElement(props)}
        </IslandFrame>
      );
    },
    dispose(): void {
      if (disposed) return;
      disposed = true;
      root.unmount();
    }
  };
}
