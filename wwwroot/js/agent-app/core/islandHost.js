// islandHost.ts — the shared loader state machine for React islands.
//
// Every migrated subtree goes through the same lifecycle: buffer the latest
// props, dynamically import the island module on first render, mount it into
// a host element created by the owning controller, and fall back permanently
// to the legacy renderer when the import or mount fails. Controllers own the
// dynamic-import thunk (so the compiled output keeps one `import('./x.js')`
// per island for the zero-load guard) and pass it in here.
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
// ---- loader ----------------------------------------------------------------
export function createIslandLoader(options) {
    let handle = null;
    let pending = null;
    let loading = false;
    let failed = false;
    let disposed = false;
    async function loadIsland() {
        try {
            const mount = await interceptedLoad(options);
            // Workspace closed while the import was in flight: discard the mount.
            if (disposed || pending === null)
                return;
            const host = options.createHost();
            if (!host)
                return;
            handle = mount(host);
            // Replay the latest props buffered while the import ran.
            handle.render(pending);
        }
        catch (err) {
            // Record and permanently fall back to the legacy renderer.
            failed = true;
            console.warn('[agent] React island "' + options.name + '" failed to load; keeping the legacy renderer.', err);
            if (!disposed && pending !== null)
                options.onLoadFailed(pending);
        }
    }
    return {
        render(props) {
            if (failed || disposed)
                return;
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
        dispose() {
            disposed = true;
            handle?.dispose();
            handle = null;
            pending = null;
        },
        hasFailed() {
            return failed;
        }
    };
}
