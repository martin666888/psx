// Manual acceptance probe. All state belongs under the supplied TestResults root.
// Usage: node smoke.mjs <installed pi root> <probe root> <launch.mjs>
import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createInterface } from 'node:readline';

const [install, probe, launcher] = process.argv.slice(2).map(value => resolve(value));
const resultsRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../../TestResults');
const probeRelative = relative(resultsRoot, probe);
if (probeRelative.startsWith('..') || isAbsolute(probeRelative)) {
  throw new Error('Pi acceptance state must stay under this repository TestResults directory.');
}
const home = join(probe, 'home');
const agent = join(home, '.pi', 'agent');
const cwd = join(probe, 'working directory 空格');
await mkdir(agent, { recursive: true });
await mkdir(cwd, { recursive: true });
const server = createServer((request, response) => {
  request.resume();
  response.writeHead(200, { 'Content-Type': 'text/event-stream' });
  const base = { id: 'test-completion', object: 'chat.completion.chunk', created: 1, model: 'test' };
  response.write(`data: ${JSON.stringify({ ...base, choices: [{ index: 0, delta: { role: 'assistant', content: 'Pi acceptance passed.' }, finish_reason: null }] })}\n\n`);
  response.write(`data: ${JSON.stringify({ ...base, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }], usage: { prompt_tokens: 5, completion_tokens: 3, total_tokens: 8 } })}\n\n`);
  response.end('data: [DONE]\n\n');
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
await writeFile(join(agent, 'models.json'), JSON.stringify({ providers: { 'psx-test': {
  baseUrl: `http://127.0.0.1:${server.address().port}/v1`, api: 'openai-completions', apiKey: 'local-test-only',
  models: [{ id: 'test', name: 'Test', reasoning: false, input: ['text'], contextWindow: 32000, maxTokens: 1024,
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } }]
} } }));
await writeFile(join(agent, 'settings.json'), JSON.stringify({ defaultProvider: 'psx-test', defaultModel: 'test', quietStartup: true }));
const piManifest = JSON.parse(await readFile(join(install, 'node_modules/@earendil-works/pi-coding-agent/package.json'), 'utf8'));
const piEntry = join(install, 'node_modules/@earendil-works/pi-coding-agent', piManifest.bin.pi);
const child = spawn(process.execPath, [launcher, join(install, 'node_modules/pi-acp/dist/index.js'), piEntry], {
  cwd, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
  env: { SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR, COMSPEC: process.env.COMSPEC,
    PATH: process.env.PATH, PATHEXT: process.env.PATHEXT,
    HOME: home, USERPROFILE: home, APPDATA: home, LOCALAPPDATA: home, TEMP: probe, TMP: probe,
    PI_CODING_AGENT_DIR: agent }
});
let stderr = '';
child.stderr.on('data', chunk => { stderr += chunk.toString(); });
const pending = new Map();
const updates = [];
createInterface({ input: child.stdout }).on('line', line => {
  try {
    const message = JSON.parse(line);
    if (message.id != null && pending.has(message.id)) {
      const { resolve, reject } = pending.get(message.id);
      pending.delete(message.id);
      if (message.error) reject(new Error(JSON.stringify(message.error))); else resolve(message.result);
    } else if (message.method === 'session/update') updates.push(message.params.update);
  } catch { /* diagnostics go to the probe artifact */ }
});
let nextId = 1;
async function call(method, params) {
  const id = nextId++;
  let timer;
  try {
    return await Promise.race([
      new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
      }),
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(`${method} timed out`)), 25000); })
    ]);
  } finally { clearTimeout(timer); pending.delete(id); }
}
try {
  const initialized = await call('initialize', { protocolVersion: 1, clientCapabilities: {}, clientInfo: { name: 'psx-probe', version: '1' } });
  const session = await call('session/new', { cwd, mcpServers: [] });
  const prompt = await call('session/prompt', { sessionId: session.sessionId, prompt: [{ type: 'text', text: 'Reply with a short test result.' }] });
  if (!updates.some(update => update.sessionUpdate === 'agent_message_chunk')) throw new Error('No streamed text');
  await call('session/load', { sessionId: session.sessionId, cwd, mcpServers: [] });
  console.log(JSON.stringify({ protocolVersion: initialized.protocolVersion, sessionCreated: true, stopReason: prompt.stopReason, restored: true, updates: updates.length }));
} finally {
  await writeFile(join(probe, 'stderr.log'), stderr);
  if (child.exitCode === null) spawnSync('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
  await new Promise(resolve => server.close(resolve));
}
