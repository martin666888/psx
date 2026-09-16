import { describe, expect, it } from 'vitest';
import { getProviderIcon } from '../../../frontend/webview/src/ProviderIcons.js';

describe('Pi provider catalog icon', () => {
    it('resolves its own mark from the icon key', () => {
        expect(getProviderIcon('pi').viewBox).toBe('0 0 24 24');
        expect(getProviderIcon('pi').paths).not.toEqual(getProviderIcon('agent').paths);
        expect(getProviderIcon('PI')).toEqual(getProviderIcon('pi'));
    });
});
