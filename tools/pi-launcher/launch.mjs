// pi-acp accepts an executable, but not a separate executable argument list.
// Route only its private command marker to portable Node + the managed Pi entry.
// This avoids cmd.exe parsing user working directories and session paths.
import childProcess from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import { pathToFileURL } from 'node:url';
import { isAbsolute } from 'node:path';
import { createInterface } from 'node:readline';

const [adapterEntry, piEntry, action] = process.argv.slice(2);
if (!adapterEntry || !piEntry || !isAbsolute(adapterEntry) || !isAbsolute(piEntry)) {
  throw new Error('Managed Pi entry paths are required.');
}

const marker = 'psx-managed-pi-rpc';
const originalSpawn = childProcess.spawn;
childProcess.spawn = function (command, args, options) {
  if (command === marker) {
    return originalSpawn(process.execPath, [piEntry, ...(args ?? [])], {
      ...options, shell: false, windowsHide: true
    });
  }
  return originalSpawn(command, args, options);
};
syncBuiltinESMExports();
process.env.PI_ACP_PI_COMMAND = marker;

if (action === '--smoke') {
  const result = childProcess.spawnSync(process.execPath, [piEntry, '--version'], {
    encoding: 'utf8', windowsHide: true, timeout: 20000
  });
  if (result.error || result.status !== 0) throw new Error('Managed Pi version probe failed.');
  // Initialize only: validate the installed adapter without creating a session,
  // reading credentials, launching tools, or sending a model request.
  const probe = originalSpawn(process.execPath, [process.argv[1], adapterEntry, piEntry], {
    stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true
  });
  probe.stderr.resume();
  const lines = createInterface({ input: probe.stdout });
  let timer;
  try {
    await new Promise((resolve, reject) => {
      timer = setTimeout(() => reject(new Error('Pi ACP initialization timed out.')), 20000);
      probe.once('error', reject);
      probe.once('exit', () => reject(new Error('Pi ACP exited before initialization.')));
      lines.on('line', line => {
        try {
          const response = JSON.parse(line);
          if (response.id === 1) {
            if (response.result?.protocolVersion === 1) resolve();
            else reject(new Error('Pi ACP protocol validation failed.'));
          }
        } catch { /* Only a valid ACP response can pass the probe. */ }
      });
      probe.stdin.end(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize',
        params: { protocolVersion: 1, clientCapabilities: {}, clientInfo: { name: 'PSX', version: '1' } } }) + '\n');
    });
  } finally {
    clearTimeout(timer);
    lines.close();
    if (probe.exitCode === null) {
      const exited = new Promise(resolve => probe.once('exit', resolve));
      probe.kill();
      await exited;
    }
  }
  process.stdout.write(result.stdout);
} else {
  await import(pathToFileURL(adapterEntry).href);
}
