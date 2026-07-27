// islandLoader.test.js — required-island loading, diagnostics and recovery.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { appModule, installAgentRuntime } from './agentHarness.js';

const { createIslandLoader } = await appModule('core/islandHost.js');
const tick = () => new Promise((resolve) => setTimeout(resolve, 0));

function host() {
  installAgentRuntime();
  const node = document.createElement('div');
  document.body.appendChild(node);
  return node;
}

async function withoutConsoleErrors(run) {
  const original = console.error;
  const errors = [];
  console.error = (...args) => errors.push(args);
  try {
    await run(errors);
  } finally {
    console.error = original;
  }
}

test('an import failure renders a non-retryable local diagnostic', async () => {
  await withoutConsoleErrors(async (errors) => {
    const node = host();
    let attempts = 0;
    const loader = createIslandLoader({
      name: 'timeline',
      host: node,
      load: async () => {
        attempts += 1;
        throw new SyntaxError('broken module');
      }
    });

    loader.render({ value: 1 });
    await tick();
    assert.equal(node.dataset.islandState, 'failed');
    assert.equal(node.querySelector('[role="alert"]').dataset.failurePhase, 'import');
    assert.match(node.textContent, /Restart PSX/);
    assert.equal(node.querySelector('button'), null);
    loader.retry();
    await tick();
    assert.equal(attempts, 1, 'same-document import failures are not retried');
    assert.equal(errors.length, 1);
  });
});

test('a mount failure retries with the latest props and a fresh handle', async () => {
  await withoutConsoleErrors(async () => {
    const node = host();
    const rendered = [];
    let attempts = 0;
    const loader = createIslandLoader({
      name: 'plan',
      host: node,
      load: async () => {
        attempts += 1;
        if (attempts === 1) {
          return () => {
            throw new Error('mount failed');
          };
        }
        return () => ({
          render(props) {
            rendered.push(props);
            node.textContent = String(props.value);
            node.dataset.islandState = 'mounted';
          },
          dispose() {}
        });
      }
    });

    loader.render({ value: 1 });
    loader.render({ value: 2 });
    await tick();
    assert.equal(node.dataset.islandState, 'failed');
    assert.equal(node.querySelector('[role="alert"]').dataset.failurePhase, 'mount');
    node.querySelector('button').click();
    await tick();
    assert.equal(attempts, 2);
    assert.deepEqual(rendered, [{ value: 2 }]);
    assert.equal(node.textContent, '2');
  });
});

test('a render failure can recover and restores the newest state', async () => {
  await withoutConsoleErrors(async () => {
    const node = host();
    const rendered = [];
    let mounts = 0;
    const loader = createIslandLoader({
      name: 'history',
      host: node,
      load: async () => () => {
        mounts += 1;
        const thisMount = mounts;
        return {
          render(props) {
            if (thisMount === 1) throw new Error('render failed');
            rendered.push(props);
            node.textContent = props.text;
            node.dataset.islandState = 'mounted';
          },
          dispose() {}
        };
      }
    });

    loader.render({ text: 'old' });
    await tick();
    assert.equal(node.querySelector('[role="alert"]').dataset.failurePhase, 'render');
    loader.render({ text: 'latest' });
    node.querySelector('button').click();
    await tick();
    assert.equal(mounts, 2);
    assert.deepEqual(rendered, [{ text: 'latest' }]);
  });
});

test('disposing during an in-flight load prevents a late mount', async () => {
  const node = host();
  let release;
  let mounted = false;
  const gate = new Promise((resolve) => {
    release = resolve;
  });
  const loader = createIslandLoader({
    name: 'runtime',
    host: node,
    load: async () => {
      await gate;
      return () => {
        mounted = true;
        return { render() {}, dispose() {} };
      };
    }
  });

  loader.render({ value: 1 });
  loader.dispose();
  release();
  await tick();
  assert.equal(mounted, false);
  assert.equal(node.childElementCount, 0);
  assert.equal(node.dataset.islandState, undefined);
});

test('one failed island does not affect an independent island', async () => {
  await withoutConsoleErrors(async () => {
    const failedHost = host();
    const healthyHost = document.createElement('div');
    document.body.appendChild(healthyHost);
    const failed = createIslandLoader({
      name: 'failed',
      host: failedHost,
      load: async () => {
        throw new Error('no module');
      }
    });
    const healthy = createIslandLoader({
      name: 'healthy',
      host: healthyHost,
      load: async () => () => ({
        render(props) {
          healthyHost.textContent = props.text;
          healthyHost.dataset.islandState = 'mounted';
        },
        dispose() {}
      })
    });

    failed.render({});
    healthy.render({ text: 'still alive' });
    await tick();
    assert.equal(failedHost.dataset.islandState, 'failed');
    assert.equal(healthyHost.dataset.islandState, 'mounted');
    assert.equal(healthyHost.textContent, 'still alive');
  });
});
