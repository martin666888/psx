# Cline CLI seed (`cline` and `@cline/cli-windows-x64`) — Apache-2.0

PSX pins `cline@3.0.53` and `@cline/cli-windows-x64@3.0.53` in
`tools/cline-seed/`. The public package contains only these manifests and the
lockfile. After explicit user confirmation, npm downloads the runtime from the
official npm registry into `runtime/cline-current/`; that installed tree is not
redistributed in the PSX ZIP. A later user-triggered toolbar update installs
the same two packages at one newer version into `runtime/cline-next/` and
promotes on the next launch.

The Cline wrapper package declares Apache-2.0 and identifies its upstream
source as <https://github.com/cline/cline> (`apps/cli`). Its production
dependencies retain their own license metadata in the downloaded npm tree.
The platform package supplies the Windows x64 executable selected through
`CLINE_BIN_PATH`; PSX always launches it through Cline's official Node wrapper
so the wrapper's OS certificate discovery remains active.

The pinned 3.0.53 lockfile can report upstream npm advisories in Cline's own
transitive tree (for example nested `undici` copies). PSX does not rewrite
that pin or vendor a different Cline release to clear them. Full still checks
that every `resolved` URL is `registry.npmjs.org` and that wrapper/platform
integrity hashes are present.
