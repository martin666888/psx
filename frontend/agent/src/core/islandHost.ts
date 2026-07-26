// islandHost.ts — the shared loader state machine for React islands.
//
// Every migrated subtree goes through the same lifecycle: buffer the latest
// props, dynamically import the island module on first render, mount it into
// a host element created by the owning controller, and fall back permanently
// to the legacy renderer when the import or mount fails. Controllers own the
// dynamic-import thunk (so the compiled output keeps one `import('./x.js')`
// per island for the zero-load guard) and pass it in here.

/** What a mounted island exposes back to its controller. */
export interface IslandHandle<TProps> {
  render(props: TProps): void;
  dispose(): void;
}

/** Mounts an island into the given host element. */
export type IslandMount<TProps> = (host: HTMLElement) => IslandHandle<TProps>;

export interface IslandLoaderOptions<TProps> {
  /** Diagnostic name used in the load-failure warning. */
  name: string;
  /** Dynamic-import thunk owned by the calling controller. */
  load: () => Promise<IslandMount<TProps>>;
  /** Creates and inserts the host element; null when the workspace is gone. */
  createHost: () => HTMLElement | null;
  /** Permanent legacy fallback, invoked with the latest buffered props. */
  onLoadFailed: (props: TProps) => void;
}

export interface IslandLoader<TProps> {
  render(props: TProps): void;
  dispose(): void;
  hasFailed(): boolean;
}

// ---- test seam (Group C) ---------------------------------------------------
//
// jsdom tests cannot make `import()` of a real sibling module fail, so the
// fallback path is exercised by intercepting island loads here. Production
// code never sets an interceptor.

type IslandLoadInterceptor = (name: string, load: () => Promise<unknown>) => Promise<unknown>;

let loadInterceptor: IslandLoadInterceptor | null = null;

/** Test-only: intercept island imports to simulate load failures. */
export function setIslandLoadInterceptorForTests(next: IslandLoadInterceptor | null): void {
  loadInterceptor = next;
}

function interceptedLoad<TProps>(options: IslandLoaderOptions<TProps>): Promise<IslandMount<TProps>> {
  if (loadInterceptor) {
    return loadInterceptor(options.name, options.load) as Promise<IslandMount<TProps>>;
  }
  return options.load();
}

// ---- loader ----------------------------------------------------------------

export function createIslandLoader<TProps>(options: IslandLoaderOptions<TProps>): IslandLoader<TProps> {
  let handle: IslandHandle<TProps> | null = null;
  let pending: TProps | null = null;
  let loading = false;
  let failed = false;
  let disposed = false;

  async function loadIsland(): Promise<void> {
    try {
      const mount = await interceptedLoad(options);
      // Workspace closed while the import was in flight: discard the mount.
      if (disposed || pending === null) return;
      const host = options.createHost();
      if (!host) return;
      handle = mount(host);
      // Replay the latest props buffered while the import ran.
      handle.render(pending);
    } catch (err) {
      // Record and permanently fall back to the legacy renderer.
      failed = true;
      console.warn(
        '[agent] React island "' + options.name + '" failed to load; keeping the legacy renderer.',
        err
      );
      if (!disposed && pending !== null) options.onLoadFailed(pending);
    }
  }

  return {
    render(props: TProps): void {
      if (failed || disposed) return;
      pending = props;
      if (handle) {
        handle.render(props);
        return;
      }
      if (!loading) {
        loading = true;
        void loadIsland();
      }
    },
    dispose(): void {
      disposed = true;
      handle?.dispose();
      handle = null;
      pending = null;
    },
    hasFailed(): boolean {
      return failed;
    }
  };
}
