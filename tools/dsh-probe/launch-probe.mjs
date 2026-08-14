#!/usr/bin/env node
// DSH P0 probe - Cluster 2b: launch `dsh web --host 127.0.0.1 --port 0` with
// the portable Node, parse the ready signal, verify HTTP 200 + boot data,
// then reap the whole process tree. DSH_HOME is redirected to a probe-owned
// directory so the probe never touches the user's real ~/.dsh.
// Artifacts: TestResults/dsh-probe/launch-report.json.
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');
const stateDir = path.join(repoRoot, 'TestResults', 'dsh-probe');
const installDir = path.join(stateDir, 'install');
const entry = path.join(installDir, 'node_modules', '@deepseek-ai', 'dsh', 'lib', 'bin.js');
const report = {
  probe: 'dsh-launch',
  node: process.version,
  started: new Date().toISOString(),
  pid: null,
  ready: null,
  exited: null,
  httpStatus: null,
  httpBodyLength: null,
  title: null,
  containsDeepSeek: null,
  exposedZeroZero: false,
  dshHomeHonored: null,
  userDshCreated: null,
  stdoutHead: '',
  stderrHead: '',
  pass: false,
};

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const killTree = (pid) =>
  new Promise((resolve) => {
    if (!pid) return resolve();
    const killer = spawn('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    killer.on('exit', resolve);
    killer.on('error', resolve);
  });

function writeReport() {
  writeFileSync(path.join(stateDir, 'launch-report.json'), JSON.stringify(report, null, 2), 'utf8');
  console.log(JSON.stringify(report, null, 2));
}

async function main() {
  if (!existsSync(entry)) {
    report.stderrHead = `entry missing: ${entry}`;
    writeReport();
    process.exit(1);
  }
  mkdirSync(stateDir, { recursive: true });
  const dshHome = path.join(stateDir, 'dsh-home');
  mkdirSync(dshHome, { recursive: true });
  const env = { ...process.env, DSH_HOME: dshHome, NO_COLOR: '1', FORCE_COLOR: '0' };

  const child = spawn(process.execPath, [entry, 'web', '--host', '127.0.0.1', '--port', '0'], {
    cwd: stateDir, // neutral cwd: never a project directory
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });
  report.pid = child.pid;

  let stdout = '';
  let stderr = '';
  child.stdout.on('data', (d) => { stdout += d.toString(); });
  child.stderr.on('data', (d) => { stderr += d.toString(); });

  // Ready signal: `dsh web: http://127.0.0.1:<port>` (printed after the
  // Loader settles). Any 0.0.0.0 / localhost alias exposure is a veto.
  const readyPattern = /dsh\s+web:\s+(https?:\/\/[^\s]+)/i;
  let readyUrl = null;
  let exited = null;
  child.on('exit', (code, signal) => { exited = { code, signal }; });
  const deadline = Date.now() + 120000;
  while (!readyUrl && !exited && Date.now() < deadline) {
    const match = readyPattern.exec(stdout);
    if (match) readyUrl = match[1];
    else await delay(200);
  }
  report.ready = readyUrl;
  report.exited = exited;
  report.stdoutHead = stdout.slice(0, 800).replace(/\x1b\[[0-9;]*m/g, '');
  report.stderrHead = stderr.slice(0, 800).replace(/\x1b\[[0-9;]*m/g, '');

  if (!readyUrl) {
    report.pass = false;
    writeReport();
    await killTree(child.pid);
    process.exit(1);
  }

  let url = null;
  try { url = new URL(readyUrl); } catch { url = null; }
  const loopbackOk = !!url && url.hostname === '127.0.0.1' && url.protocol === 'http:';
  report.exposedZeroZero = /0\.0\.0\.0/.test(stdout + stderr);

  try {
    const response = await fetch(url);
    const body = await response.text();
    report.httpStatus = response.status;
    report.httpBodyLength = body.length;
    const title = /<title>([^<]*)<\/title>/i.exec(body);
    report.title = title ? title[1] : null;
    report.containsDeepSeek = /deepseek/i.test(body);
  } catch (error) {
    report.httpError = String(error);
  }

  report.dshHomeHonored = existsSync(path.join(dshHome, 'profiles'))
    || existsSync(path.join(dshHome, 'storages'))
    ? true
    : null;
  // DSH boot also creates ~/.dsh/{profiles,storages} unconditionally even
  // when DSH_HOME is redirected — observed, never deleted by the probe.
  report.userDshCreated = existsSync(path.join(homedir(), '.dsh'));

  report.pass = !!loopbackOk && !report.exposedZeroZero && report.httpStatus === 200
    && report.httpBodyLength > 1000 && report.containsDeepSeek;
  writeReport();

  await killTree(child.pid);
  process.exit(report.pass ? 0 : 1);
}

main().catch((error) => {
  report.stderrHead = String(error && error.stack ? error.stack : error);
  writeReport();
  process.exit(1);
});
