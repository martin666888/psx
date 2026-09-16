# Pi in Agent mode

Pi is the fifth curated managed ACP provider (`acp-pi`). Choose **Pi** from
the create menu. Its workspace uses the same conversation, Composer, history,
model/config controls, attention states and runtime toolbar as other providers.

## Installation and updates

The portable package includes Pi and pi-acp under `tools/pi`, plus Portable
Node and `tools/pi-launcher/launch.mjs`. Selecting Pi works immediately;
there is no first-use runtime download or install confirmation. The release
build installs the committed seed lock for `@earendil-works/pi-coding-agent`
**0.85.1** and `pi-acp` **0.0.33**, including Windows optional dependencies,
and validates Pi version and ACP initialization. Build-time installation skips
lifecycle scripts; the shipped entries are verified by the smoke probes.
Login/API credentials are still configured by the user in Pi.

A valid self-updated `runtime/pi-current` takes precedence over the bundled
baseline. If it is absent or invalid, PSX uses `tools/pi` directly. The seed
remains available for explicit repair when no valid baseline exists.
Update queries both official latest versions, rejects downgrades and versions
below the supported floor, and installs exact versions into `runtime/pi-next`.
Validation checks manifests, bounded package entry paths, Pi `--version` and
ACP initialization. A successful update becomes active on the next PSX launch;
the running workspace continues using its original bundled or updated tree. Startup promotion
is local only. Failed/cancelled staging does not replace the current tree.

The launcher redirects only the adapter's private Pi command marker to
`<portable node> <managed Pi entry>`, with an argument array and no shell. It
does not modify installed third-party files. This is a compatibility shim for
the adapter's executable-only override and Windows `.cmd` quoting behavior.

## Login, history and capabilities

The login action opens managed Pi in a terminal. Use Pi's `/login` there, then
retry the Agent workspace. `/terminal` starts the same managed Pi and passes
the bound session ID when present. Pi owns credentials and session files under
`PI_CODING_AGENT_DIR` or `~/.pi/agent`. pi-acp also maintains its upstream
`~/.pi/pi-acp/session-map.json`; its session-file discovery provides recovery
when a map entry is missing.

PSX restores through the negotiated ACP session capabilities. The supported
adapter exposes session loading and returns model/thinking configuration and
its slash-command catalog. PSX's existing command allowlist remains authoritative.

Pi executes its own filesystem and bash tools. The adapter does not delegate
these through ACP `fs/*` or `terminal/*`, so PSX does not advertise those
capabilities for Pi. Its extension confirmation requests can use ACP permission
requests; this is not a universal pre-execution approval gate for Pi tools.
The adapter does not forward client-provided MCP servers into Pi. Configure
any Pi MCP extension in Pi itself; PSX does not invent a native MCP catalog.

## Configuration and usage

The read-only configuration report scans user-level `settings.json`,
`models.json`, `auth.json` and `skills/**/SKILL.md`. It shows the default model,
custom model descriptors, stored credential provider names, and skill directory
names. It never exposes credential/header values or URL query/userinfo. Each
JSON configuration read has the shared 2 MiB cap. Project configuration and
extension-specific MCP settings are not scanned.

Usage is labeled **PSX sessions only**. The source reads Pi v3 JSONL beneath
the agent directory's `sessions` folder, checks session headers against PSX
session IDs, and streams exact assistant, compaction and branch-summary usage.
Input, output, cache-read and cache-write counts are separate and must match
the persisted total. Duplicate exact records are counted once. A fork's
inherited entry IDs are excluded using its parent file; missing or unreadable
lineage yields an explicit completeness gap. Deduplication and lineage indexes
are bounded. Missing logs, unsupported schemas and invalid rows never become
an apparently complete zero total. Custom session directories outside this
tree are currently reported as missing logs rather than guessed.

## Verification

Unit and process-level tests cover the provider, entry-path validation,
installation, failure/cancellation, staging and promotion, config redaction,
usage attribution, duplicate records and forks. The Web gate checks the Pi
catalog icon alongside the shared workspace/history tests.

`tools/pi-launcher/smoke.mjs` is an optional Windows acceptance probe. Pass an
installed Pi seed tree, a probe directory under `TestResults`, and the absolute
launcher path. Run it with the pinned Portable Node. It uses isolated homes,
a loopback mock model API, and a cwd containing spaces and Chinese text to
exercise initialization, session creation, streaming and history load. It
does not require a real provider key. Real provider OAuth and subjective UI
checks remain manual.

Upstream references:

- [pi-acp source and capabilities](https://github.com/svkozak/pi-acp)
- [Pi session format](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/src/core/session-manager.ts)
- [Pi custom models](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/docs/models.md)
