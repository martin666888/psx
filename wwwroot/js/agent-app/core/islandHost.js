// islandHost.ts — required React-island loader with local diagnostics.
//
// Every migrated region has one renderer: its React island. The loader keeps
// the latest props while a dynamic import is pending and contains failures to
// the island host. It never falls back to an alternate DOM renderer.
let loadInterceptor = null;
/** Test-only: intercept island imports to simulate load failures. */
export function setIslandLoadInterceptorForTests(next) {
    loadInterceptor = next;
}
function interceptedLoad(options) {
    if (loadInterceptor) {
        return loadInterceptor(options.name, options.load);
    }
    return options.load();
}
function errorMessage(error) {
    if (error instanceof Error && error.message.trim())
        return error.message.trim();
    const text = String(error ?? '').trim();
    return text || 'Unknown error';
}
function renderFailure(host, name, phase, error, retry) {
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
    console.error('[agent] React island "' + name + '" failed during ' + phase + ': ' + errorMessage(error), error);
}
export function createIslandLoader(options) {
    const legacyOptions = 'createHost' in options ? options : null;
    const stableHost = 'host' in options ? options.host : null;
    let handle = null;
    let latestProps;
    let hasProps = false;
    let state = 'idle';
    let failurePhase = null;
    let generation = 0;
    const disposeHandle = () => {
        const mounted = handle;
        handle = null;
        if (!mounted)
            return;
        try {
            mounted.dispose();
        }
        catch (error) {
            console.error('[agent] React island "' + options.name + '" failed while disposing.', error);
        }
    };
    const fail = (phase, error) => {
        if (state === 'disposed' || state === 'failed')
            return;
        state = 'failed';
        failurePhase = phase;
        generation += 1;
        disposeHandle();
        if (legacyOptions) {
            console.warn('[agent] React island "' + options.name + '" failed to load; keeping the legacy renderer.', error);
            if (hasProps)
                legacyOptions.onLoadFailed(latestProps);
            return;
        }
        renderFailure(stableHost, options.name, phase, error, phase === 'import' ? null : () => loader.retry());
    };
    const reportFailure = (error, phase) => {
        // React may report during its commit. Defer unmounting until that commit
        // has unwound to avoid recursively updating the same root.
        queueMicrotask(() => fail(phase, error));
    };
    const loadIsland = async () => {
        if (state === 'loading' || state === 'mounted' || state === 'disposed')
            return;
        const currentGeneration = ++generation;
        state = 'loading';
        failurePhase = null;
        if (stableHost) {
            stableHost.dataset.islandState = 'loading';
            stableHost.setAttribute('aria-busy', 'true');
            stableHost.replaceChildren();
        }
        let mount;
        try {
            mount = await interceptedLoad(options);
        }
        catch (error) {
            if (generation === currentGeneration) {
                fail('import', error);
            }
            else if (legacyOptions) {
                // Preserve the old diagnostic contract for an import that settles
                // after its workspace has already closed, without reviving fallback.
                console.warn('[agent] React island "' + options.name + '" failed to load; keeping the legacy renderer.', error);
            }
            return;
        }
        if (generation !== currentGeneration)
            return;
        const host = stableHost ?? legacyOptions?.createHost() ?? null;
        if (!host) {
            if (legacyOptions) {
                state = 'failed';
                failurePhase = 'mount';
                console.warn('[agent] React island "' + options.name + '" has no host element; keeping the legacy renderer.');
                if (hasProps)
                    legacyOptions.onLoadFailed(latestProps);
            }
            else {
                fail('mount', new Error('Island host is unavailable.'));
            }
            return;
        }
        try {
            handle = mount(host, reportFailure);
            state = 'mounted';
        }
        catch (error) {
            fail('mount', error);
            return;
        }
        if (hasProps && handle) {
            try {
                handle.render(latestProps);
            }
            catch (error) {
                fail('render', error);
            }
        }
    };
    const loader = {
        render(props) {
            if (state === 'disposed')
                return;
            latestProps = props;
            hasProps = true;
            if (state === 'mounted' && handle) {
                try {
                    handle.render(props);
                }
                catch (error) {
                    fail('render', error);
                }
                return;
            }
            if (state === 'idle')
                void loadIsland();
        },
        retry() {
            if (state !== 'failed' || failurePhase === 'import')
                return;
            state = 'idle';
            failurePhase = null;
            stableHost.replaceChildren();
            delete stableHost.dataset.islandState;
            if (hasProps)
                void loadIsland();
        },
        dispose() {
            if (state === 'disposed')
                return;
            state = 'disposed';
            generation += 1;
            disposeHandle();
            if (stableHost) {
                stableHost.replaceChildren();
                delete stableHost.dataset.islandState;
                stableHost.removeAttribute('aria-busy');
            }
        },
        hasFailed() {
            return state === 'failed';
        }
    };
    return loader;
}
