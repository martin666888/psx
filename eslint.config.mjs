// eslint.config.mjs — lint:web gate for the WebView frontend sources and the
// web build tooling. Correctness-first rule set: @eslint/js recommended plus
// typescript-eslint recommended (syntax-only, no type-aware rules — tsc owns
// type checking through typecheck:web). Formatting stays out of scope.
import js from '@eslint/js';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    ignores: [
      'wwwroot/**',
      'node_modules/**',
      'TestResults/**',
      'bin/**',
      'obj/**'
    ]
  },
  js.configs.recommended,
  tseslint.configs.recommended,
  {
    files: ['frontend/**/*.{js,ts,tsx}', 'tools/*.mjs'],
    languageOptions: {
      globals: {
        window: 'readonly',
        document: 'readonly',
        navigator: 'readonly',
        localStorage: 'readonly',
        console: 'readonly',
        requestAnimationFrame: 'readonly',
        cancelAnimationFrame: 'readonly',
        ResizeObserver: 'readonly',
        TextEncoder: 'readonly',
        TextDecoder: 'readonly',
        btoa: 'readonly',
        atob: 'readonly',
        performance: 'readonly',
        crypto: 'readonly',
        setTimeout: 'readonly',
        clearTimeout: 'readonly',
        URL: 'readonly',
        Buffer: 'readonly',
        process: 'readonly'
      }
    },
    rules: {
      // tsc (noUnusedLocals/noUnusedParameters) already gates unused code for
      // TS; keep the underscore convention for intentionally unused values.
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }
      ]
    }
  }
);
