// Runs only from a clean trusted checkout. Never execute code from the input artifact.
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import { compareSemVer, parseSemVer } from './dsh-semver.mjs';

const pkg = '@deepseek-ai/dsh';
const seedPath = 'tools/dsh-seed';
const catalogPath = 'tools/dsh-locks/catalog.json';
const runtimePath = 'Services/DshWebRuntime.cs';
const releasePath = 'tools/build-release.ps1';
const sha = bytes => createHash('sha256').update(bytes).digest('hex').toUpperCase();
const json = bytes => JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
const fail = message => { throw new Error(message); };
const exactVersion = version => {
    if (!parseSemVer(version)) fail('Invalid exact SemVer');
    return version;
};
const git = (root, ...args) => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8' }).trim();

export function chooseVersion(requested, published, seed, blocked) {
    const valid = published.filter(v => parseSemVer(v)).sort((a, b) => compareSemVer(b, a) || a.localeCompare(b));
    const version = exactVersion(requested || valid[0]);
    if (!valid.includes(version)) fail('Selected version is not published');
    if (compareSemVer(version, seed) < 0) fail('Seed downgrade refused');
    if (blocked.some(e => e.version === version)) fail('Selected version is blocked');
    return version;
}

function regularFile(root, relative) {
    const absolute = path.resolve(root, relative);
    if (!absolute.startsWith(path.resolve(root) + path.sep)) fail('Path escapes root');
    let current = path.resolve(root);
    if (fs.lstatSync(current).isSymbolicLink()) fail('Linked root refused');
    for (const component of relative.split('/')) {
        current = path.join(current, component);
        if (fs.lstatSync(current).isSymbolicLink()) fail('Linked input refused');
    }
    const stat = fs.statSync(absolute);
    if (!stat.isFile() || stat.size > 32 * 1024 * 1024) fail('Invalid or oversized artifact file');
    return fs.readFileSync(absolute);
}

export function validateLock(manifestBytes, lockBytes, version, integrity) {
    const manifest = json(manifestBytes);
    const lock = json(lockBytes);
    if (Object.keys(manifest).sort().join() !== ['name', 'version', 'private', 'description', 'dependencies'].sort().join()
        || manifest.name !== 'psx-dsh-runtime' || manifest.version !== '0.1.0'
        || Object.keys(manifest.dependencies ?? {}).join() !== pkg
        || manifest.dependencies[pkg] !== version || manifest.private !== true) fail('Unexpected root manifest');
    if (lock.lockfileVersion !== 3 || lock.packages?.['']?.dependencies?.[pkg] !== version
        || lock.packages?.['']?.scripts) fail('Invalid lock root');
    const sri = /^sha512-[A-Za-z0-9+/]{86}==$/;
    if (!sri.test(integrity)) fail('Invalid official SRI');
    for (const [key, entry] of Object.entries(lock.packages ?? {})) {
        if (key === '') continue;
        if (!key.startsWith('node_modules/') || key.includes('\\')
            || key.split('/').some(p => !p || p === '.' || p === '..') || entry.link) fail('Invalid package path');
        const url = new URL(entry.resolved);
        if (url.origin !== 'https://registry.npmjs.org' || url.username || url.password
            || url.search || url.hash || !sri.test(entry.integrity)) fail('Invalid package origin/SRI');
    }
    const root = lock.packages[`node_modules/${pkg}`];
    if (root?.version !== version || root?.integrity !== integrity) fail('Root SRI/version mismatch');
}

function replaceOne(text, pattern, replacement) {
    if ([...text.matchAll(new RegExp(pattern.source, 'g'))].length !== 1) fail(`Expected one field: ${pattern}`);
    return text.replace(pattern, replacement);
}

export async function resolveSelection(root, requested, output) {
    const baseCommitSha = git(root, 'rev-parse', 'HEAD');
    const seed = json(regularFile(root, `${seedPath}/package.json`)).dependencies[pkg];
    const catalog = json(regularFile(root, catalogPath));
    const response = await globalThis.fetch('https://registry.npmjs.org/@deepseek-ai%2fdsh', { signal: globalThis.AbortSignal.timeout(60000) });
    if (!response.ok) fail(`Official registry failed: ${response.status}`);
    const metadata = await response.json();
    const version = chooseVersion(requested, Object.keys(metadata.versions), seed, catalog.blockedVersions);
    const integrity = metadata.versions[version].dist.integrity;
    const old = catalog.entries.find(e => e.version === version);
    if (old && old.dshSri !== integrity) fail('SECURITY EVENT: same-version SRI changed');
    const npmrc = regularFile(root, `${seedPath}/.npmrc`).toString();
    const selection = { baseCommitSha, version, integrity, npmrc, npmrcSha256: sha(npmrc),
        status: 'selected; CI and clean Windows acceptance pending' };
    fs.writeFileSync(output, JSON.stringify(selection, null, 2) + '\n');
    return selection;
}

export function prepareCandidate(root, input, selection, output) {
    exactVersion(selection.version);
    if (!/^[0-9a-f]{40}$/.test(selection.baseCommitSha)
        || git(root, 'rev-parse', 'HEAD') !== selection.baseCommitSha) fail('Base commit drift');
    if (git(root, 'status', '--porcelain', '--untracked-files=normal')) fail('Candidate checkout must be clean');
    const version = selection.version;
    const read = relative => regularFile(root, relative);
    const catalog = json(read(catalogPath));
    const seed = json(read(`${seedPath}/package.json`)).dependencies[pkg];
    chooseVersion(version, [version], seed, catalog.blockedVersions);
    const files = fs.readdirSync(input).sort();
    if (files.join() !== ['catalog.json', 'npmrc', 'package-lock.json', 'package.json'].sort().join()) fail('Unexpected artifact files');
    const npmrc = regularFile(input, 'npmrc');
    if (!npmrc.equals(read(`${seedPath}/.npmrc`)) || sha(npmrc) !== selection.npmrcSha256) fail('Install configuration drift');
    const manifest = regularFile(input, 'package.json');
    const lock = regularFile(input, 'package-lock.json');
    validateLock(manifest, lock, version, selection.integrity);
    if (manifest.includes(13) || lock.includes(13)) fail('Lock artifacts must be LF only');
    // Accept only the selected entry. Everything else is reconstructed from trusted HEAD.
    const candidate = json(regularFile(input, 'catalog.json'));
    const matches = candidate.entries.filter(e => e.version === version);
    if (matches.length !== 1) fail('Expected one selected catalog entry');
    const entry = matches[0];
    const keys = ['version', 'lockSha256', 'packageSha256', 'lockfileVersion', 'generatedByNpm', 'dshSri', 'smokePassed'];
    if (Object.keys(entry).sort().join() !== keys.sort().join()
        || entry.lockSha256?.toUpperCase() !== sha(lock) || entry.packageSha256?.toUpperCase() !== sha(manifest)
        || entry.dshSri !== selection.integrity || entry.smokePassed !== true || entry.lockfileVersion !== 3
        || !parseSemVer(entry.generatedByNpm)) fail('Invalid selected catalog evidence');
    const existing = catalog.entries.find(e => e.version === version);
    if (existing) {
        if (!isDeepStrictEqual(existing, entry)
            || !read(`tools/dsh-locks/locks/${version}/package.json`).equals(manifest)
            || !read(`tools/dsh-locks/locks/${version}/package-lock.json`).equals(lock)) fail('Existing entry changed');
    }
    // Build the complete approved write set before changing any tracked file.
    const writes = new Map();
    const removed = [];
    if (!existing) {
        catalog.entries.push(entry);
        catalog.entries.sort((a, b) => compareSemVer(b.version, a.version));
        for (const old of catalog.entries.splice(5)) {
            exactVersion(old.version);
            for (const name of ['package.json', 'package-lock.json']) removed.push(`tools/dsh-locks/locks/${old.version}/${name}`);
        }
        writes.set(catalogPath, Buffer.from(JSON.stringify(catalog, null, 2) + '\n'));
        writes.set(`tools/dsh-locks/locks/${version}/package.json`, manifest);
        writes.set(`tools/dsh-locks/locks/${version}/package-lock.json`, lock);
    }
    if (!isDeepStrictEqual(candidate, catalog)) fail('Unexpected catalog changes');
    writes.set(`${seedPath}/package.json`, manifest);
    writes.set(`${seedPath}/package-lock.json`, lock);
    let runtime = read(runtimePath).toString();
    runtime = replaceOne(runtime, /public const string SeededPackageVersion = "[^"]+";/,
        `public const string SeededPackageVersion = "${version}";`);
    runtime = replaceOne(runtime, /private static readonly HashSet<string> HistoricalSeedVersions = new\(StringComparer.Ordinal\)\s*\{[^}]*\};/,
        block => block.includes(`"${version}"`) ? block : block.replace(/(\s*)\};$/, `$1    "${version}",$1};`));
    writes.set(runtimePath, Buffer.from(runtime));
    let release = read(releasePath).toString();
    for (const [field, value] of Object.entries({ DshPinnedVersion: version, DshSeedLockExpectedSha: sha(lock),
        DshLockCatalogExpectedSha: sha(writes.get(catalogPath) ?? read(catalogPath)) })) {
        release = replaceOne(release, new RegExp(`\\$${field} = "[^"]+"`), () => `$${field} = "${value}"`);
    }
    writes.set(releasePath, Buffer.from(release));
    const docs = [
        ['AGENTS.md', /First install uses the `[^`]+` seed lock/, `First install uses the \`${version}\` seed lock`],
        ['tools/dsh-locks/README.md', /当前首次安装 seed 为 `[^`]+`/, `当前首次安装 seed 为 \`${version}\``],
    ];
    for (const [file, pattern, replacement] of docs) writes.set(file, Buffer.from(replaceOne(read(file).toString(), pattern, replacement)));
    fs.mkdirSync(output, { recursive: true });
    for (const file of removed) {
        read(file); // checks containment and links before deletion of individual files
    }
    for (const file of removed) {
        fs.unlinkSync(path.join(root, file));
    }
    // Empty pruned directories also violate the on-disk catalog invariant.
    for (const directory of new Set(removed.map(file => path.dirname(path.join(root, file))))) {
        fs.rmdirSync(directory); // non-recursive: unexpected contents are never deleted
    }
    for (const [file, bytes] of writes) {
        const destination = path.join(root, file);
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        fs.writeFileSync(destination, bytes);
    }
    const allowed = [...writes.keys(), ...removed];
    git(root, 'add', '--intent-to-add', '--', ...writes.keys());
    const changed = git(root, 'diff', '--name-only').split(/\r?\n/).filter(Boolean);
    if (changed.some(file => !allowed.includes(file))) fail('Out-of-scope patch');
    git(root, 'diff', '--binary', `--output=${path.resolve(output, 'dsh-candidate.patch')}`, '--', ...allowed);
    const report = { ...selection, status: 'Static candidate checks passed; Fast, package and clean Windows acceptance pending',
        nodeVersion: read(releasePath).toString().match(/\$PortableNodeVersion = "([^"]+)"/)[1],
        npmVersion: entry.generatedByNpm, seedLockSha256: sha(lock),
        changedFiles: changed, hashes: Object.fromEntries([...writes].map(([file, bytes]) => [file, sha(bytes)])),
        commands: { solve: 'npm install <package>@<version> --save-exact --omit=dev --include=optional --engine-strict --no-audit --no-fund --package-lock-only --ignore-scripts',
            install: 'npm ci --omit=dev --include=optional --engine-strict --no-audit --no-fund',
            launch: 'dsh web --host 127.0.0.1 --port 0 --no-open' },
        configuration: 'Official registry, isolated cache and empty user npmrc; seed .npmrc copied verbatim. See generator at baseCommitSha for full commands.' };
    fs.writeFileSync(path.join(output, 'candidate.json'), JSON.stringify(report, null, 2) + '\n');
    return report;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const [mode, ...args] = process.argv.slice(2);
    if (mode === 'resolve') await resolveSelection(args[0], args[1], args[2]);
    else if (mode === 'prepare') prepareCandidate(args[0], args[1], json(fs.readFileSync(args[2])), args[3]);
    else fail('Usage: dsh-candidate.mjs resolve <repo> <version-or-empty> <selection.json> | prepare <repo> <input> <selection.json> <output>');
}
