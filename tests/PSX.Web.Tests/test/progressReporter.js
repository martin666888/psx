import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..', '..');
const outputPath = path.join(repoRoot, 'TestResults', 'web', 'vitest-progress.json');

function relativeModuleId(testModule) {
  return path.relative(repoRoot, testModule.moduleId).replaceAll(path.sep, '/');
}

function writeProgress(progress) {
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  const temporaryPath = `${outputPath}.${process.pid}.tmp`;
  fs.writeFileSync(temporaryPath, `${JSON.stringify(progress, null, 2)}\n`, 'utf8');
  fs.renameSync(temporaryPath, outputPath);
}

export default class ProgressReporter {
  constructor() {
    this.progress = {
      schemaVersion: 1,
      lastStartedFile: null,
      lastCompletedFile: null
    };
  }

  onTestRunStart() {
    writeProgress(this.progress);
  }

  onTestModuleStart(testModule) {
    this.progress.lastStartedFile = relativeModuleId(testModule);
    writeProgress(this.progress);
  }

  onTestModuleEnd(testModule) {
    this.progress.lastCompletedFile = relativeModuleId(testModule);
    writeProgress(this.progress);
  }
}
