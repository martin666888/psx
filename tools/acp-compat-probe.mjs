#!/usr/bin/env node
/**
 * Local Phase-0 ACP probe (NOT for CI): initialize → session/new → optional cancel → exit.
 * Set ACP_PROBE_PROMPT=1 to also send a short prompt (requires auth + network).
 */
import { spawn } from 'node:child_process';

const sep = process.argv.indexOf('--');
const cmd = process.argv[sep + 1];
const args = process.argv.slice(sep + 2);
if (!cmd) {
  console.error('Usage: node acp-compat-probe.mjs -- <command> [args...]');
  process.exit(2);
}

const timeoutMs = Number(process.env.ACP_PROBE_TIMEOUT_MS || 45000);
const doPrompt = process.env.ACP_PROBE_PROMPT === '1';
const cwd = process.env.ACP_PROBE_CWD || process.cwd();

const child = spawn(cmd, args, {
  stdio: ['pipe', 'pipe', 'pipe'],
  windowsHide: true,
  shell: process.platform === 'win32' && /\.(cmd|bat)$/i.test(cmd),
  cwd,
});

const pending = new Map();
let nextId = 1;
let stdoutBuf = '';
const notes = [];

function log(msg, data) {
  notes.push({ msg, data });
  console.log(msg, data ? JSON.stringify(data, null, 2) : '');
}

function request(method, params) {
  const id = nextId++;
  const payload = { jsonrpc: '2.0', id, method, params };
  child.stdin.write(JSON.stringify(payload) + '\n');
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
  });
}

function respond(id, result) {
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, result }) + '\n');
}

const timer = setTimeout(() => {
  log('TIMEOUT');
  shutdown(1);
}, timeoutMs);

function shutdown(code) {
  clearTimeout(timer);
  try {
    child.stdin.end();
  } catch {
    /* ignore */
  }
  setTimeout(() => {
    try {
      child.kill();
    } catch {
      /* ignore */
    }
    process.exit(code);
  }, 1500);
}

child.stdout.setEncoding('utf8');
child.stderr.setEncoding('utf8');
child.stderr.on('data', (c) => process.stderr.write(c));
child.stdout.on('data', (chunk) => {
  stdoutBuf += chunk;
  const lines = stdoutBuf.split(/\r?\n/);
  stdoutBuf = lines.pop() ?? '';
  for (const line of lines) {
    if (!line.trim()) continue;
    let msg;
    try {
      msg = JSON.parse(line);
    } catch {
      log('non-json', line);
      continue;
    }
    if (msg.method && msg.id != null) {
      // Answer reverse RPCs with safe stubs so the agent does not hang.
      if (msg.method === 'session/request_permission') {
        respond(msg.id, { outcome: { outcome: 'cancelled' } });
        continue;
      }
      if (msg.method === 'fs/read_text_file' || msg.method === 'fs/write_text_file') {
        respond(msg.id, { content: '' });
        continue;
      }
      if (msg.method?.startsWith('terminal/')) {
        respond(msg.id, {});
        continue;
      }
      respond(msg.id, {});
      continue;
    }
    if (msg.id != null && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(Object.assign(new Error(msg.error.message || 'rpc error'), { rpc: msg.error }));
      else resolve(msg.result);
    }
  }
});

child.on('error', (err) => {
  log('spawn-error', String(err));
  shutdown(1);
});

(async () => {
  try {
    const init = await request('initialize', {
      protocolVersion: 1,
      clientCapabilities: {
        fs: { readTextFile: true, writeTextFile: true },
        terminal: true,
      },
      clientInfo: { name: 'psx-compat-probe', version: '0.1.0' },
    });
    log('initialize', {
      agentInfo: init.agentInfo,
      agentCapabilities: init.agentCapabilities,
      authMethods: init.authMethods?.map((m) => m.id),
    });

    await request('authenticate', { methodId: init.authMethods?.[0]?.id }).catch((err) => {
      log('authenticate-skipped-or-failed', err.rpc || String(err));
    });

    const session = await request('session/new', { cwd, mcpServers: [] });
    log('session/new', session);

    if (doPrompt && session?.sessionId) {
      try {
        const promptResult = await request('session/prompt', {
          sessionId: session.sessionId,
          prompt: [{ type: 'text', text: '用一句话回复：你好' }],
        });
        log('session/prompt', promptResult);
      } catch (err) {
        log('session/prompt-failed', err.rpc || String(err));
      }
    }

    if (session?.sessionId) {
      await request('session/cancel', { sessionId: session.sessionId }).catch((err) => {
        log('session/cancel', err.rpc || String(err));
      });
    }

    log('OK');
    shutdown(0);
  } catch (err) {
    log('FAILED', err.rpc || String(err));
    shutdown(1);
  }
})();
