Offline React ESM bundles for the Agent frontend import map.

React version:   19.2.8
esbuild version: 0.28.1
Regenerate with: npm run vendor:react (only when upgrading React).

react-core.js bundles react, react/jsx-runtime, react-dom and
react-dom/client into one self-contained module graph (single React
instance by construction). The other files are generated facades that
re-export named bindings for the import map bare specifiers.

File hashes and the allowed import surface live in vendor-manifest.json;
the static guard test in tests/PSX.Web.Tests verifies both, so never
edit these files by hand.
