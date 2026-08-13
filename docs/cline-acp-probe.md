# Cline ACP compatibility baseline

Pinned runtime: `cline@3.0.53` + `@cline/cli-windows-x64@3.0.53`, Windows x64.

Run the opt-in probe only against an install beneath `TestResults/`:

```powershell
node tools/cline-acp-probe.mjs --runtime-root TestResults/cline-runtime-probe-<id>
```

Set `CLINE_PROBE_CONCURRENCY=1` to run two independent ACP processes. The
default is one process to respect the repository's workstation-memory safety
rule. The report is written atomically to
`TestResults/cline-acp-probe/report.json` and contains no credentials or raw
conversation content.

Verified without authentication:

- Official wrapper starts the platform executable over stdio ACP.
- `initialize` returns protocol 1 and agent version 3.0.53.
- Local backend is forced through `CLINE_SESSION_BACKEND_MODE=local`.
- The wrapper uses `CLINE_BIN_PATH` and writes its combined system CA bundle.
- Wrapper and platform versions can be validated independently.
- Closing stdin and terminating the process releases the runtime directory.

Not accepted as verified without an authenticated deterministic endpoint:

- Advertised image support reaches the model request.
- Plan/Act changes alter the next model request.
- ACP usage or plan updates are emitted with exact semantics.

Do not invent fake credentials, loopback model servers, or guessed Cline
provider protocols to close those gaps. If a later probe has a documented,
isolated fixture that Cline itself accepts, re-run this script and only then
relax compatibility.

## Exact usage (C4)

Cline persists session history under `~/.cline/data/` with SQLite databases
and task JSON. That layout does not, by itself, prove:

- a unique join from a PSX-owned ACP session ID
- exact input / output / cache-read / cache-creation counters
- that parent sessions and subtasks will not be double-counted

Adding a SQLite dependency or estimating tokens is out of scope. Production
keeps `UsageSource = null` and the Usage panel shows Cline as unavailable.

Consequently PSX compatibility disables prompt images and publishes only a
proven-safe Act mode for this baseline. Exact usage remains unavailable
rather than estimated. Toolbar self-update may install a newer Cline into
`runtime/cline-next/`; that does not relax these compatibility gates until a
later authenticated probe says otherwise.
