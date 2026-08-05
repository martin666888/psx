# Qwen Code Bundled Runtime — Third-Party Notices

PSX bundles the Qwen Code ACP runtime (`@qwen-code/qwen-code`) under
`tools/qwen/` in the portable release. The runtime and its production
dependencies are installed from the pinned lockfile in `tools/qwen-seed/`
(constrained to Windows x64).

Primary package:

- `@qwen-code/qwen-code@0.21.5` — Apache-2.0. See `licenses/qwen/LICENSE`.

Windows x64 production/optional dependencies observed in the pinned install
(re-check after any lockfile bump):

- `@lydell/node-pty@1.2.0-beta.10` (+ win32-x64) — MIT
- `@teddyzhu/clipboard@0.0.5` (+ win32-x64-msvc) — MIT
- `sharp@0.34.5` — Apache-2.0
- `@img/sharp-win32-x64@0.34.5` — Apache-2.0 AND LGPL-3.0-or-later
- `@img/colour@1.1.0` — MIT
- `detect-libc@2.1.2` — Apache-2.0
- `node-gyp-build@4.8.4` — MIT
- `semver@7.8.5` — ISC
- `@qwen-code/audio-capture@0.21.5` — bundled companion (see package tree)

macOS/Linux platform variants listed as optional dependencies in the lockfile
are NOT installed into the release (the seed `.npmrc` pins `os=win32`/`cpu=x64`).

Upstream package license files remain inside
`tools/qwen/node_modules/**` in the portable zip when present.
