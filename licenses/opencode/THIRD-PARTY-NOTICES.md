# OpenCode Bundled Runtime — Third-Party Notices

PSX bundles the OpenCode ACP runtime under `tools/opencode/` in the portable
release. The runtime is installed from the pinned lockfile in
`tools/opencode-seed/` (constrained to Windows x64).

Primary package:

- `opencode-windows-x64@1.18.14` — MIT (license text taken from the
  `opencode-ai` distribution; the platform package itself ships no LICENSE
  file). See `licenses/opencode/LICENSE`.

Notes on the artifact shape:

- PSX depends on the platform package directly, never the `opencode-ai`
  wrapper (whose `bin/opencode.exe` is a 479-byte placeholder hydrated by a
  postinstall script). The platform package has no production dependencies
  and no install scripts.
- `bin/opencode.exe` is a self-contained native executable (~175 MB) that
  embeds the Bun runtime (MIT, https://github.com/oven-sh/bun). Third-party
  components compiled into the binary are listed by upstream at
  https://github.com/sst/opencode.
- The AVX2-less variant `opencode-windows-x64-baseline` is never bundled; on
  machines without AVX2 it is installed into `runtime/opencode-current` only
  after explicit user confirmation (same MIT license).

macOS/Linux platform variants listed in the upstream optional dependencies
are NOT installed into the release (the seed `.npmrc` pins
`os=win32`/`cpu=x64`).
