#!/usr/bin/env node
/**
 * Release/CI-safe ACP smoke: start → initialize → await result → close stdin / kill.
 * No session/new, no prompt (no login / network model dependency).
 *
 * Usage: node acp-initialize-probe.mjs -- <command> [args...]
 */
import { spawn } from 'node:child_process';

const sep = process.argv.indexOf('--');
const cmd = process.argv[sep + 1];
const args = process.argv.slice(sep + 2);
if (!cmd) {
  console.error('Usage: node acp-initialize-probe.mjs -- <command> [args...]');
  process.exit(2);
}

const timeoutMs = Number(process.env.ACP_PROBE_TIMEOUT_MS || 20000);
const child = spawn(cmd, args, {
  stdio: ['pipe', 'pipe', 'pipe'],
  windowsHide: true,
  shell: process.platform === 'win32' && /\.(cmd|bat)$/i.test(cmd),
});

let stdout = '';
let stderr = '';
let resolved = false;

const init = {
  jsonrpc: '2.0',
  id: 1,
  method: 'initialize',
  params: {
    protocolVersion: 1,
    clientCapabilities: {
      fs: { readTextFile: true, writeTextFile: true },
      terminal: true,
    },
    clientInfo: { name: 'psx-acp-probe', version: '0.1.0' },
  },
};

function finish(ok, detail) {
  if (resolved) return;
  resolved = true;
  clearTimeout(timer);
  try {
    child.stdin.end();
  } catch {
    /* ignore */
  }
  const killer = setTimeout(() => {
    try {
      child.kill();
    } catch {
      /* ignore */
    }
  }, 2000);
  child.once('exit', () => clearTimeout(killer));
  console.log(JSON.stringify({ ok, detail, stdout, stderr }, null, 2));
  process.exit(ok ? 0 : 1);
}

const timer = setTimeout(() => finish(false, 'timeout waiting for initialize result'), timeoutMs);

child.stdout.setEncoding('utf8');
child.stderr.setEncoding('utf8');
child.stdout.on('data', (chunk) => {
  stdout += chunk;
  for (const line of chunk.split(/\r?\n/)) {
    if (!line.trim()) continue;
    try {
      const msg = JSON.parse(line);
      if (msg.id === 1 && msg.result) {
        finish(true, { initializeResult: msg.result });
      } else if (msg.id === 1 && msg.error) {
        finish(false, { initializeError: msg.error });
      }
    } catch {
      /* non-JSON line */
    }
  }
});
child.stderr.on('data', (chunk) => {
  stderr += chunk;
});
child.on('error', (err) => finish(false, { spawnError: String(err) }));
child.on('exit', (code, signal) => {
  if (!resolved) finish(false, { earlyExit: { code, signal } });
});

child.stdin.write(JSON.stringify(init) + '\n');
