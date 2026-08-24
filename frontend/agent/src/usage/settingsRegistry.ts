// settingsRegistry.ts — npm download-source labels for the settings dialog.
// Wire keys match C# DshRegistryKey / environment.json (`official` | `npmmirror`).

export type DshRegistryKey = 'official' | 'npmmirror';

export const DSH_REGISTRY_KEYS = ['official', 'npmmirror'] as const;

export const DSH_REGISTRY_LABELS: Record<DshRegistryKey, string> = {
  official: 'npmjs.org',
  npmmirror: 'npmmirror.com'
};

// Registry notes moved into the settings locales (registry.noteOfficial / noteNpmmirror).

export function safeDshRegistry(value: unknown): DshRegistryKey {
  return value === 'npmmirror' ? 'npmmirror' : 'official';
}
