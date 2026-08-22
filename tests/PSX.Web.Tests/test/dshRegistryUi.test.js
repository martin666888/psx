import assert from 'node:assert/strict';
import { describe, it } from 'vitest';
import { dshSwitchCta, dshSourceLabel, otherDshRegistry } from '../../../frontend/webview/src/DshRegistryUi.js';

describe('DSH registry CTAs', () => {
  it('offers the other source on timeout for first install', () => {
    const cta = dshSwitchCta({
      state: 'failed',
      runtimeErrorClass: 'registry_timeout',
      registryKey: 'official'
    });
    assert.equal(cta?.command, 'retry_install_with_registry');
    assert.equal(cta?.registry, 'npmmirror');
  });

  it('offers official recheck when a mirror check is not found', () => {
    const cta = dshSwitchCta({
      state: 'ready',
      currentVersion: '0.1.0-rc.6',
      updateErrorClass: 'registry_not_found',
      registryKey: 'npmmirror'
    });
    assert.equal(cta?.command, 'recheck_with_registry');
    assert.equal(cta?.registry, 'official');
  });

  it('does not suggest the mirror after official E404', () => {
    assert.equal(dshSwitchCta({
      state: 'ready',
      currentVersion: '0.1.0-rc.6',
      updateErrorClass: 'registry_not_found',
      registryKey: 'official'
    }), null);
  });

  it('hides the switch CTA for catalog and lock failures', () => {
    assert.equal(dshSwitchCta({
      state: 'ready',
      currentVersion: '0.1.0-rc.6',
      updateErrorClass: 'lock_unavailable',
      registryKey: 'npmmirror'
    }), null);
    assert.equal(dshSwitchCta({
      state: 'ready',
      currentVersion: '0.1.0-rc.6',
      updateErrorClass: 'catalog_corrupt',
      registryKey: 'npmmirror'
    }), null);
  });

  it('labels the in-flight source only when operationRegistryKey is set', () => {
    assert.match(dshSourceLabel({ registryKey: 'official' }), /当前默认下载源/);
    assert.match(dshSourceLabel({ registryKey: 'official', operationRegistryKey: '' }), /当前默认下载源/);
    assert.match(dshSourceLabel({ registryKey: 'official', operationRegistryKey: 'npmmirror' }), /本次来源/);
    assert.equal(otherDshRegistry('official'), 'npmmirror');
  });
});
