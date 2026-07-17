import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = path.resolve(testDirectory, '..', '..', '..');
export const generatedBundlePath = path.join(repositoryRoot, 'TestResults', 'web', 'runtime', 'agent-production.bundle.js');

// Kept in sync with the js/ script order in wwwroot/index.html by
// test/scriptOrder.test.js. TerminalManager.js and main.js are intentionally
// excluded from the test bundle.
export const scriptPaths = [
  'wwwroot/js/BridgeMessages.js',
  'wwwroot/js/Bridge.js',
  'wwwroot/js/AgentThreadManager.js',
  'wwwroot/js/agent/composer.js',
  'wwwroot/js/agent/config.js',
  'wwwroot/js/agent/commands.js',
  'wwwroot/js/agent/attachments.js',
  'wwwroot/js/agent/runtime.js',
  'wwwroot/js/agent/thread.js',
  'wwwroot/js/agent/messages.js',
  'wwwroot/js/agent/inspector.js',
  'wwwroot/js/agent/plan.js',
  'wwwroot/js/agent/thinking.js',
  'wwwroot/js/agent/tools.js',
  'wwwroot/js/agent/permissions.js',
  'wwwroot/js/agent/modeTransition.js',
  'wwwroot/js/agent/elicitation.js',
  'wwwroot/js/agent/markdown.js',
  'wwwroot/js/agent/scroll.js'
];

export function buildProductionBundle() {
  const source = scriptPaths
    .map((relativePath) => fs.readFileSync(path.join(repositoryRoot, relativePath), 'utf8'))
    .join('\n;\n');
  const bundle = `(function () {\n${source}\n;globalThis.Bridge = Bridge; globalThis.AgentThreadManager = AgentThreadManager; globalThis.BridgeSendType = BridgeSendType; globalThis.BridgeEventType = BridgeEventType;\n})();\n`;
  fs.mkdirSync(path.dirname(generatedBundlePath), { recursive: true });
  fs.writeFileSync(generatedBundlePath, bundle, 'utf8');
  return generatedBundlePath;
}
