import githubDark from '@shikijs/themes/github-dark';
import githubLight from '@shikijs/themes/github-light';
import { createHighlighterCore } from 'shiki/core';
import { createOnigurumaEngine } from 'shiki/engine/oniguruma';

const languageLoaders = {
  c: () => import('@shikijs/langs/c').then((module) => module.default),
  cpp: () => import('@shikijs/langs/cpp').then((module) => module.default),
  csharp: () => import('@shikijs/langs/csharp').then((module) => module.default),
  css: () => import('@shikijs/langs/css').then((module) => module.default),
  diff: () => import('@shikijs/langs/diff').then((module) => module.default),
  docker: () => import('@shikijs/langs/docker').then((module) => module.default),
  go: () => import('@shikijs/langs/go').then((module) => module.default),
  html: () => import('@shikijs/langs/html').then((module) => module.default),
  java: () => import('@shikijs/langs/java').then((module) => module.default),
  javascript: () => import('@shikijs/langs/javascript').then((module) => module.default),
  json: () => import('@shikijs/langs/json').then((module) => module.default),
  jsx: () => import('@shikijs/langs/jsx').then((module) => module.default),
  markdown: () => import('@shikijs/langs/markdown').then((module) => module.default),
  powershell: () => import('@shikijs/langs/powershell').then((module) => module.default),
  python: () => import('@shikijs/langs/python').then((module) => module.default),
  rust: () => import('@shikijs/langs/rust').then((module) => module.default),
  shellscript: () => import('@shikijs/langs/shellscript').then((module) => module.default),
  sql: () => import('@shikijs/langs/sql').then((module) => module.default),
  tsx: () => import('@shikijs/langs/tsx').then((module) => module.default),
  typescript: () => import('@shikijs/langs/typescript').then((module) => module.default),
  xml: () => import('@shikijs/langs/xml').then((module) => module.default),
  yaml: () => import('@shikijs/langs/yaml').then((module) => module.default)
};

const highlighter = createHighlighterCore({
  themes: [githubLight, githubDark],
  langs: [],
  engine: createOnigurumaEngine(import('shiki/wasm'))
});
const languageJobs = new Map<string, Promise<void>>();

async function ensureLanguage(language: keyof typeof languageLoaders): Promise<Awaited<typeof highlighter>> {
  const instance = await highlighter;
  if (instance.getLoadedLanguages().includes(language)) return instance;
  let job = languageJobs.get(language);
  if (!job) {
    job = languageLoaders[language]().then(async (registrations) => {
      await instance.loadLanguage(...registrations);
    });
    languageJobs.set(language, job);
  }
  await job;
  return instance;
}

self.addEventListener('message', (event: MessageEvent<{ id: number; code: string; language: string }>) => {
  const { id, code, language } = event.data;
  if (!(language in languageLoaders)) {
    self.postMessage({ id, ok: false });
    return;
  }
  void ensureLanguage(language as keyof typeof languageLoaders)
    .then((instance) => {
      const result = instance.codeToTokens(code, {
        lang: language,
        themes: { light: 'github-light', dark: 'github-dark' }
      });
      self.postMessage({ id, ok: true, result });
    })
    .catch(() => self.postMessage({ id, ok: false }));
});
