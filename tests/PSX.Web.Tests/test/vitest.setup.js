// Shared Vitest setup. The old node --test runner had no global setup; keep
// this minimal so tests stay self-describing. React act() support is enabled
// per-file by the tests that render React trees, exactly as before.
import { afterAll } from 'vitest';

process.env.TZ ||= 'UTC';

// Island loaders import their React module dynamically after a host event.
// A test file may legitimately finish while such an import is still in
// flight; give pending dynamic imports one macrotask window to settle so the
// worker does not tear down its module rpc mid-fetch (noise, not a failure).
afterAll(async () => {
  await new Promise((resolve) => setTimeout(resolve, 50));
});
