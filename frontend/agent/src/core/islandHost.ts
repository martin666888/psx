// islandHost.ts — required React-island loader with local diagnostics.
//
// Every migrated region has one renderer: its React island. The loader keeps
// the latest props while a dynamic import is pending and contains failures to
// the island host. It never falls back to an alternate DOM renderer.

/** What a mounted island exposes back to its controller. */
export interface IslandHandle<TProps> {
  render(props: TProps): void;
  dispose(): void;
}

export type IslandFailurePhase = 'mount' | 'render';
export type IslandFailureReporter = (error: unknown, phase: IslandFailurePhase) => void;

/** Mounts one independent React root into the given host element. */
export type IslandMount<TProps> = (
  host: HTMLElement,
  reportFailure: IslandFailureReporter
) => IslandHandle<TProps>;

export interface ReactOnlyIslandLoaderOptions<TProps> {
  name: string;
  load: () => Promise<IslandMount<TProps>>;
  /** Stable, controller-owned host for the lifetime of this loader. */
  host: HTMLElement;
}

/**
 * Transitional shape retained only for the first migration checkpoint.
 * It lets not-yet-retired controllers keep their existing fallback while the
 * React-only loader contract is established underneath them.
 */
export interface LegacyIslandLoaderOptions<TProps> {
  name: string;
  load: () => Promise<IslandMount<TProps>>;
  createHost: () => HTMLElement | null;
  onLoadFailed: (props: TProps) => void;
}

export type IslandLoaderOptions<TProps> =
  | ReactOnlyIslandLoaderOptions<TProps>
  | LegacyIslandLoaderOptions<TProps>;

export interface IslandLoader<TProps> {
  render(props: TProps): void;
  /** Retries mount/render failures. Import failures require a new Document. */
  retry(): void;
  dispose(): void;
  /** Transitional query for controllers removed at the next checkpoints. */
  hasFailed(): boolean;
}

// Tests simulate import failures here without changing production specifiers.
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

function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message.trim()) return error.message.trim();
  const text = String(error ?? '').trim();
  return text || 'Unknown error';
}

function renderFailure(
  host: HTMLElement,
  name: string,
  phase: 'import' | IslandFailurePhase,
  error: unknown,
  retry: (() => void) | null
): void {
  host.replaceChildren();
  host.dataset.islandState = 'failed';
  host.removeAttribute('aria-busy');

  const card = document.createElement('section');
  card.className = 'agent-island-error';
  card.setAttribute('role', 'alert');
  card.dataset.island = name;
  card.dataset.failurePhase = phase;

  const title = document.createElement('strong');
  title.className = 'agent-island-error-title';
  title.textContent = name + ' could not be displayed';

  const message = document.createElement('p');
  message.className = 'agent-island-error-message';
  message.textContent =
    phase === 'import'
      ? 'Restart PSX to reload this interface. If the problem continues, replace or reinstall the PSX package.'
      : 'This part of the interface stopped unexpectedly.';

  card.append(title, message);
  if (retry) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'agent-island-error-retry';
    button.textContent = 'Retry';
    button.addEventListener('click', retry);
    card.appendChild(button);
  }
  host.appendChild(card);

  console.error(
    '[agent] React island "' + name + '" failed during ' + phase + ': ' + errorMessage(error),
    error
  );
}

export function createIslandLoader<TProps>(options: IslandLoaderOptions<TProps>): IslandLoader<TProps> {
  const legacyOptions =
    'createHost' in options ? options as LegacyIslandLoaderOptions<TProps> : null;
  const stableHost =
    'host' in options ? options.host : null;
  let handle: IslandHandle<TProps> | null = null;
  let latestProps!: TProps;
  let hasProps = false;
  let state: 'idle' | 'loading' | 'mounted' | 'failed' | 'disposed' = 'idle';
  let failurePhase: 'import' | IslandFailurePhase | null = null;
  let generation = 0;

  const disposeHandle = (): void => {
    const mounted = handle;
    handle = null;
    if (!mounted) return;
    try {
      mounted.dispose();
    } catch (error) {
      console.error('[agent] React island "' + options.name + '" failed while disposing.', error);
    }
  };

  const fail = (phase: 'import' | IslandFailurePhase, error: unknown): void => {
    if (state === 'disposed' || state === 'failed') return;
    state = 'failed';
    failurePhase = phase;
    generation += 1;
    disposeHandle();
    if (legacyOptions) {
      console.warn(
        '[agent] React island "' + options.name + '" failed to load; keeping the legacy renderer.',
        error
      );
      if (hasProps) legacyOptions.onLoadFailed(latestProps);
      return;
    }
    renderFailure(
      stableHost!,
      options.name,
      phase,
      error,
      phase === 'import' ? null : () => loader.retry()
    );
  };

  const reportFailure: IslandFailureReporter = (error, phase) => {
    // React may report during its commit. Defer unmounting until that commit
    // has unwound to avoid recursively updating the same root.
    queueMicrotask(() => fail(phase, error));
  };

  const loadIsland = async (): Promise<void> => {
    if (state === 'loading' || state === 'mounted' || state === 'disposed') return;
    const currentGeneration = ++generation;
    state = 'loading';
    failurePhase = null;
    if (stableHost) {
      stableHost.dataset.islandState = 'loading';
      stableHost.setAttribute('aria-busy', 'true');
      stableHost.replaceChildren();
    }

    let mount: IslandMount<TProps>;
    try {
      mount = await interceptedLoad(options);
    } catch (error) {
      if (generation === currentGeneration) {
        fail('import', error);
      } else if (legacyOptions) {
        // Preserve the old diagnostic contract for an import that settles
        // after its workspace has already closed, without reviving fallback.
        console.warn(
          '[agent] React island "' + options.name + '" failed to load; keeping the legacy renderer.',
          error
        );
      }
      return;
    }
    if (generation !== currentGeneration) return;

    const host = stableHost ?? legacyOptions?.createHost() ?? null;
    if (!host) {
      if (legacyOptions) {
        state = 'failed';
        failurePhase = 'mount';
        console.warn(
          '[agent] React island "' + options.name + '" has no host element; keeping the legacy renderer.'
        );
        if (hasProps) legacyOptions.onLoadFailed(latestProps);
      } else {
        fail('mount', new Error('Island host is unavailable.'));
      }
      return;
    }

    try {
      handle = mount(host, reportFailure);
      state = 'mounted';
    } catch (error) {
      fail('mount', error);
      return;
    }
    if (hasProps && handle) {
      try {
        handle.render(latestProps);
      } catch (error) {
        fail('render', error);
      }
    }
  };

  const loader: IslandLoader<TProps> = {
    render(props: TProps): void {
      if (state === 'disposed') return;
      latestProps = props;
      hasProps = true;
      if (state === 'mounted' && handle) {
        try {
          handle.render(props);
        } catch (error) {
          fail('render', error);
        }
        return;
      }
      if (state === 'idle') void loadIsland();
    },

    retry(): void {
      if (state !== 'failed' || failurePhase === 'import') return;
      state = 'idle';
      failurePhase = null;
      stableHost!.replaceChildren();
      delete stableHost!.dataset.islandState;
      if (hasProps) void loadIsland();
    },

    dispose(): void {
      if (state === 'disposed') return;
      state = 'disposed';
      generation += 1;
      disposeHandle();
      if (stableHost) {
        stableHost.replaceChildren();
        delete stableHost.dataset.islandState;
        stableHost.removeAttribute('aria-busy');
      }
    },

    hasFailed(): boolean {
      return state === 'failed';
    }
  };

  return loader;
}
