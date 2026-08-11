export const codeLanguageAliases = new Map<string, string>([
  ['bash', 'shellscript'], ['sh', 'shellscript'], ['shell', 'shellscript'],
  ['js', 'javascript'], ['mjs', 'javascript'], ['cjs', 'javascript'],
  ['ts', 'typescript'], ['py', 'python'], ['cs', 'csharp'],
  ['ps1', 'powershell'], ['pwsh', 'powershell'], ['yml', 'yaml'],
  ['md', 'markdown'], ['htm', 'html'], ['dockerfile', 'docker']
]);

export const supportedCodeLanguages = [
  'c', 'cpp', 'csharp', 'css', 'diff', 'docker', 'go', 'html', 'java',
  'javascript', 'json', 'jsx', 'markdown', 'powershell', 'python', 'rust',
  'shellscript', 'sql', 'tsx', 'typescript', 'xml', 'yaml'
] as const;

export const codeRendererLanguages = [
  ...supportedCodeLanguages,
  ...codeLanguageAliases.keys()
];

export function normalizeCodeLanguage(language: string): string {
  const normalized = language.trim().toLowerCase();
  return codeLanguageAliases.get(normalized) ?? normalized;
}
