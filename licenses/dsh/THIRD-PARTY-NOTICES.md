DeepSeek Harness (DSH) — @deepseek-ai/dsh
==========================================

PSX ships only the installation seed for DeepSeek Harness under
`tools/dsh-seed/` (package manifest, pinned lockfile and registry-pinning
.npmrc for the 0.1.3-alpha.2 release). The DSH runtime itself is NOT
redistributed in the portable package: it is downloaded from the official
npm registry (registry.npmjs.org) into `runtime/dsh-current/` only after
the user explicitly confirms the installation. Later versions are downloaded
only when the user explicitly requests and confirms an update; every installed
version remains subject to its own license terms.

The `dsh web` server runs as an independent loopback process supervised by
PSX (Job Object reaping, readiness and health checks). DSH itself owns its
models, keys, projects, sessions, MCP, skills and configuration under the
user's `~/.dsh` directory; PSX only mediates the tab, column, web container
and session-export save path.

License: MIT. The full license text is provided alongside this notice in
`LICENSE`.
