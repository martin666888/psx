#!/usr/bin/env node
/**
 * Opt-in Cline ACP compatibility probe. It refuses to touch a runtime or state
 * outside TestResults, never uses real credentials, and writes one sanitized
 * JSON report. Set CLINE_PROBE_CONCURRENCY=1 to exercise two simultaneous ACP
 * processes; the default stays single-process for workstation memory safety.
 */
import { spawn } from 'node:child_process';
import { mkdir, readFile, rename, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

const repositoryRoot = path.resolve(import.meta.dirname, '..');
const testResultsRoot = path.join(repositoryRoot, 'TestResults');
const args = process.argv.slice(2);
const rootIndex = args.indexOf('--runtime-root');
const runtimeRoot = rootIndex >= 0 ? path.resolve(args[rootIndex + 1] || '') : '';
if (!runtimeRoot || !isWithin(testResultsRoot, runtimeRoot)) {
  console.error('Usage: node tools/cline-acp-probe.mjs --runtime-root <TestResults/...>');
  process.exit(2);
}

const wrapper = path.join(runtimeRoot, 'node_modules', 'cline', 'bin', 'cline');
const executable = path.join(runtimeRoot, 'node_modules', '@cline', 'cli-windows-x64', 'bin', 'cline.exe');
const nodePath = process.env.CLINE_PROBE_NODE || process.execPath;
const reportRoot = path.join(testResultsRoot, 'cline-acp-probe');
const runRoot = path.join(reportRoot, `run-${Date.now()}-${process.pid}`);
await mkdir(runRoot, { recursive: true });

const runs = [];
const first = runClient('primary');
if (process.env.CLINE_PROBE_CONCURRENCY === '1') {
  runs.push(...await Promise.all([first, runClient('secondary')]));
} else {
  runs.push(await first);
}

const packageEvidence = await collectPackageEvidence(runtimeRoot);
const movableAfterExit = await verifyDirectoryMovable(runtimeRoot);
const report = {
  schemaVersion: 1,
  generatedAt: new Date().toISOString(),
  pinnedVersion: '3.0.53',
  packageEvidence,
  runs,
  gates: {
    initialize: runs.every(run => run.initialize?.agentInfo?.version === '3.0.53'),
    localBackendForced: runs.every(run => run.environment.localBackend === 'local'),
    noAmbientProviderOrDataOverride: runs.every(run => run.environment.overridesCleared),
    imageDelivery: 'unverified_without_authenticated_model_fixture',
    modeSemantics: 'unverified_without_authenticated_model_fixture',
    usageUpdates: 'not_observed_without_authenticated_prompt',
    planUpdates: 'not_observed_without_authenticated_prompt',
    processCleanupReleasedRuntime: movableAfterExit,
  },
};

const temporaryReport = path.join(reportRoot, `report-${process.pid}.tmp`);
const reportPath = path.join(reportRoot, 'report.json');
await writeFile(temporaryReport, JSON.stringify(report, null, 2), 'utf8');
await rename(temporaryReport, reportPath);
console.log(JSON.stringify({ ok: report.gates.initialize && movableAfterExit, reportPath, report }, null, 2));
process.exit(report.gates.initialize && movableAfterExit ? 0 : 1);

async function runClient(name) {
  const stateRoot = path.join(runRoot, name);
  await mkdir(stateRoot, { recursive: true });
  const environment = {
    ...process.env,
    CLINE_BIN_PATH: executable,
    CLINE_NO_AUTO_UPDATE: '1',
    CLINE_SESSION_BACKEND_MODE: 'local',
    // Probe-only isolation. Production launch policy deliberately clears both.
    CLINE_DIR: stateRoot,
    CLINE_DATA_DIR: path.join(stateRoot, 'data'),
  };
  delete environment.CLINE_PROVIDER;
  delete environment.CLINE_MODEL;
  delete environment.CLINE_HUB_ADDRESS;
  delete environment.CLINE_API_KEY;

  const client = spawn(nodePath, [wrapper, '--acp', '--auto-approve', 'false'], {
    cwd: runtimeRoot,
    env: environment,
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });
  let stderr = '';
  let buffer = '';
  let nextId = 0;
  const pending = new Map();
  client.stderr.setEncoding('utf8');
  client.stderr.on('data', chunk => { stderr = bounded(stderr + chunk, 4096); });
  client.stdout.setEncoding('utf8');
  client.stdout.on('data', chunk => {
    buffer += chunk;
    const lines = buffer.split(/\r?\n/);
    buffer = lines.pop() || '';
    for (const line of lines) handleLine(line);
  });

  function handleLine(line) {
    let message;
    try { message = JSON.parse(line); } catch { return; }
    if (message.method && message.id != null) {
      const result = message.method === 'session/request_permission'
        ? { outcome: { outcome: 'cancelled' } }
        : {};
      client.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: message.id, result }) + '\n');
      return;
    }
    const waiter = pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    if (message.error) {
      waiter.reject(message.error);
      return;
    }
    waiter.resolve(message.result);
  }

  function request(method, params, timeoutMs = 20_000) {
    const id = ++nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(id);
        reject({ code: 'probe_timeout', message: `${method} timed out` });
      }, timeoutMs);
      pending.set(id, {
        resolve: result => { clearTimeout(timer); resolve(result); },
        reject: error => { clearTimeout(timer); reject(error); },
      });
      client.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
  }

  let initialize = null;
  let sessionNew = null;
  let sessionNewError = null;
  try {
    initialize = await request('initialize', {
      protocolVersion: 1,
      clientCapabilities: {
        fs: { readTextFile: true, writeTextFile: true },
        terminal: true,
      },
      clientInfo: { name: 'psx-cline-probe', version: '1' },
    });
    try {
      sessionNew = await request('session/new', { cwd: runtimeRoot, mcpServers: [] }, 15_000);
    } catch (error) {
      sessionNewError = sanitizeRpcError(error);
    }
    if (sessionNew?.sessionId) {
      try { await request('session/cancel', { sessionId: sessionNew.sessionId }, 5_000); } catch { /* best effort */ }
    }
  } finally {
    client.stdin.end();
    await waitForExit(client, 5_000);
  }

  return {
    name,
    initialize: initialize ? {
      protocolVersion: initialize.protocolVersion,
      agentInfo: initialize.agentInfo,
      promptCapabilities: initialize.agentCapabilities?.promptCapabilities,
      loadSession: initialize.agentCapabilities?.loadSession === true,
      authMethodIds: Array.isArray(initialize.authMethods)
        ? initialize.authMethods.map(method => String(method?.id || '')).filter(Boolean)
        : [],
    } : null,
    sessionNew: sessionNew ? { created: true, hasSessionId: Boolean(sessionNew.sessionId) } : null,
    sessionNewError,
    stderr: sanitizeText(stderr),
    caBundleCreated: await exists(path.join(stateRoot, 'cli-node-extra-ca-certs.pem')),
    environment: {
      localBackend: environment.CLINE_SESSION_BACKEND_MODE,
      overridesCleared: !environment.CLINE_PROVIDER
        && !environment.CLINE_MODEL
        && !environment.CLINE_HUB_ADDRESS
        && !environment.CLINE_API_KEY,
    },
  };
}

async function collectPackageEvidence(root) {
  const wrapperManifest = JSON.parse(await readFile(path.join(root, 'node_modules', 'cline', 'package.json'), 'utf8'));
  const platformManifest = JSON.parse(await readFile(path.join(root, 'node_modules', '@cline', 'cli-windows-x64', 'package.json'), 'utf8'));
  return {
    wrapper: { name: wrapperManifest.name, version: wrapperManifest.version, license: wrapperManifest.license },
    platform: { name: platformManifest.name, version: platformManifest.version },
    versionsMatch: wrapperManifest.version === platformManifest.version,
  };
}

async function verifyDirectoryMovable(root) {
  const moved = `${root}.move-probe-${process.pid}`;
  try {
    await rename(root, moved);
    await rename(moved, root);
    return true;
  } catch {
    try { await rename(moved, root); } catch { /* keep original diagnostic state */ }
    return false;
  }
}

async function waitForExit(child, timeoutMs) {
  if (child.exitCode != null) return;
  await Promise.race([
    new Promise(resolve => child.once('exit', resolve)),
    new Promise(resolve => setTimeout(resolve, timeoutMs)),
  ]);
  if (child.exitCode == null) {
    child.kill();
    await new Promise(resolve => child.once('exit', resolve));
  }
}

function sanitizeRpcError(error) {
  return { code: error?.code ?? 'rpc_error', message: bounded(String(error?.message || 'RPC error'), 256) };
}

function sanitizeText(value) {
  let sanitized = value;
  for (const [source, replacement] of [[runtimeRoot, '<runtime>'], [repositoryRoot, '<repo>']]) {
    sanitized = sanitized.replace(new RegExp(escapeRegExp(source), 'gi'), replacement);
  }
  // Never preserve an unexpected Windows absolute path from an upstream error.
  sanitized = sanitized.replace(/[A-Za-z]:\\[^\r\n"}]*/g, '<path>');
  return bounded(sanitized, 4096);
}

function bounded(value, limit) { return value.length <= limit ? value : value.slice(-limit); }
function escapeRegExp(value) { return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }
function isWithin(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative && !relative.startsWith('..') && !path.isAbsolute(relative);
}
async function exists(file) { try { return (await stat(file)).isFile(); } catch { return false; } }
