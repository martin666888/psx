import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { chooseVersion, prepareCandidate, validateLock } from './dsh-candidate.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const hash = bytes => createHash('sha256').update(bytes).digest('hex').toUpperCase();
const run = (root, ...args) => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const seed = '0.1.5-rc.2';
const candidateVersion = '0.1.5-rc.3';

test('selection includes prereleases, rejects downgrade, blocked and unpublished versions', () => {
    assert.equal(chooseVersion('', [seed, candidateVersion], seed, []), candidateVersion);
    assert.throws(() => chooseVersion(seed, [seed], candidateVersion, []), /downgrade/);
    assert.throws(() => chooseVersion('', [candidateVersion], seed, [{ version: candidateVersion }]), /blocked/);
    assert.throws(() => chooseVersion('../bad', [seed], seed, []), /SemVer/);
    assert.throws(() => chooseVersion('9.0.0', [seed], seed, []), /published/);
});

function fixture(fillCatalog = false) {
    const parent = path.join(repo, 'TestResults', 'dsh-candidate');
    fs.mkdirSync(parent, { recursive: true });
    const root = fs.mkdtempSync(path.join(parent, 'case-'));
    // Isolated tiny git repository: never mutate or clean the user's checkout.
    for (const file of ['Services/DshWebRuntime.cs', 'tools/build-release.ps1', 'AGENTS.md', 'tools/dsh-locks/README.md',
        'tools/dsh-locks/catalog.json', 'tools/dsh-seed/package.json', 'tools/dsh-seed/package-lock.json', 'tools/dsh-seed/.npmrc']) {
        fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
        fs.copyFileSync(path.join(repo, file), path.join(root, file));
    }
    if (!fillCatalog) {
        fs.cpSync(path.join(repo, 'tools/dsh-locks/locks'), path.join(root, 'tools/dsh-locks/locks'), { recursive: true });
    } else {
        const catalogFile = path.join(root, 'tools/dsh-locks/catalog.json');
        const catalog = JSON.parse(fs.readFileSync(catalogFile));
        const current = JSON.parse(fs.readFileSync(path.join(root, 'tools/dsh-seed/package.json'))).dependencies['@deepseek-ai/dsh'];
        const template = catalog.entries.find(e => e.version === current);
        catalog.entries = [];
        for (const version of [current, '0.1.4-test.3', '0.1.4-test.2', '0.1.4-test.1', '0.1.3-alpha.2']) {
            const directory = path.join(root, 'tools/dsh-locks/locks', version);
            fs.mkdirSync(directory, { recursive: true });
            const manifest = JSON.parse(fs.readFileSync(path.join(root, 'tools/dsh-seed/package.json')));
            manifest.dependencies['@deepseek-ai/dsh'] = version;
            const lock = JSON.parse(fs.readFileSync(path.join(root, 'tools/dsh-seed/package-lock.json')));
            lock.packages[''].dependencies['@deepseek-ai/dsh'] = version;
            lock.packages['node_modules/@deepseek-ai/dsh'].version = version;
            fs.writeFileSync(path.join(directory, 'package.json'), JSON.stringify(manifest));
            fs.writeFileSync(path.join(directory, 'package-lock.json'), JSON.stringify(lock));
            catalog.entries.push({ ...template, version,
                packageSha256: hash(JSON.stringify(manifest)), lockSha256: hash(JSON.stringify(lock)) });
        }
        fs.writeFileSync(catalogFile, JSON.stringify(catalog));
    }
    run(root, 'init', '-q');
    fs.copyFileSync(path.join(repo, '.gitattributes'), path.join(root, '.gitattributes'));
    run(root, 'config', 'core.autocrlf', 'true');
    run(root, 'add', '.');
    run(root, '-c', 'user.name=DSH Test', '-c', 'user.email=dsh-test@example.invalid', 'commit', '-qm', 'fixture');
    const input = fs.mkdtempSync(path.join(parent, 'input-'));
    const base = JSON.parse(fs.readFileSync(path.join(root, 'tools/dsh-locks/catalog.json')));
    const manifest = JSON.parse(fs.readFileSync(path.join(root, 'tools/dsh-seed/package.json')));
    const lock = JSON.parse(fs.readFileSync(path.join(root, 'tools/dsh-seed/package-lock.json')));
    const current = manifest.dependencies['@deepseek-ai/dsh'];
    const version = `${Number(current.split('.')[0]) + 1}.0.0-candidate`;
    manifest.dependencies['@deepseek-ai/dsh'] = version;
    lock.packages[''].dependencies['@deepseek-ai/dsh'] = version;
    lock.packages['node_modules/@deepseek-ai/dsh'].version = version;
    const manifestBytes = Buffer.from(JSON.stringify(manifest) + '\n');
    const lockBytes = Buffer.from(JSON.stringify(lock) + '\n');
    const old = base.entries.find(e => e.version === current);
    const entry = { ...old, version, packageSha256: hash(manifestBytes), lockSha256: hash(lockBytes) };
    fs.writeFileSync(path.join(input, 'package.json'), manifestBytes);
    fs.writeFileSync(path.join(input, 'package-lock.json'), lockBytes);
    fs.writeFileSync(path.join(input, 'catalog.json'), JSON.stringify({ ...base, entries: [entry, ...base.entries].slice(0, 5) }));
    const npmrc = fs.readFileSync(path.join(root, 'tools/dsh-seed/.npmrc'));
    fs.writeFileSync(path.join(input, 'npmrc'), npmrc);
    return { root, input, version, selection: { baseCommitSha: run(root, 'rev-parse', 'HEAD'), version,
        integrity: entry.dshSri, npmrc: npmrc.toString(), npmrcSha256: hash(npmrc) },
        output: fs.mkdtempSync(path.join(parent, 'output-')) };
}

test('candidate produces complete patch from data without changing assertions or unrelated files', () => {
    const f = fixture();
    const report = prepareCandidate(f.root, f.input, f.selection, f.output);
    assert.ok(report.changedFiles.includes('tools/build-release.ps1'));
    assert.match(report.status, /Static candidate checks passed/);
    const runtime = fs.readFileSync(path.join(f.root, 'Services/DshWebRuntime.cs'), 'utf8');
    assert.ok(runtime.includes(`SeededPackageVersion = "${f.version}"`));
    assert.ok(runtime.includes('"0.1.5-rc.1"'));
    assert.ok(fs.readFileSync(path.join(f.output, 'dsh-candidate.patch'), 'utf8').includes(`locks/${f.version}/package-lock.json`));
    run(f.root, 'diff', '--check');
});

test('candidate rejects base, config, file-scope, integrity and existing-byte drift before mutation', () => {
    for (const attack of ['base', 'config', 'extra', 'hash', 'dirty', 'existing', 'catalog']) {
        const f = fixture();
        if (attack === 'base') f.selection.baseCommitSha = '0'.repeat(40);
        if (attack === 'config') fs.appendFileSync(path.join(f.input, 'npmrc'), 'legacy-peer-deps=true\n');
        if (attack === 'extra') fs.writeFileSync(path.join(f.input, 'evil.ps1'), 'bad');
        if (attack === 'hash') f.selection.integrity = 'sha512-' + 'A'.repeat(86) + '==';
        if (attack === 'dirty') fs.appendFileSync(path.join(f.root, 'AGENTS.md'), 'drift');
        if (attack === 'catalog') {
            const candidate = JSON.parse(fs.readFileSync(path.join(f.input, 'catalog.json')));
            candidate.blockedVersions = [{ version: '99.0.0', reason: 'smoke_failed' }];
            fs.writeFileSync(path.join(f.input, 'catalog.json'), JSON.stringify(candidate));
        }
        if (attack === 'existing') {
            const current = JSON.parse(fs.readFileSync(path.join(f.root, 'tools/dsh-seed/package.json'))).dependencies['@deepseek-ai/dsh'];
            f.selection.version = current;
        }
        const before = run(f.root, 'diff');
        assert.throws(() => prepareCandidate(f.root, f.input, f.selection, f.output), undefined, attack);
        assert.equal(run(f.root, 'diff'), before);
    }
});

test('static lock validation refuses foreign origin, links and root scripts', () => {
    const manifest = fs.readFileSync(path.join(repo, 'tools/dsh-seed/package.json'));
    const lock = JSON.parse(fs.readFileSync(path.join(repo, 'tools/dsh-seed/package-lock.json')));
    const entry = lock.packages['node_modules/@deepseek-ai/dsh'];
    const version = entry.version;
    const sri = entry.integrity;
    validateLock(manifest, Buffer.from(JSON.stringify(lock)), version, sri);
    entry.resolved = 'https://evil.invalid/dsh.tgz';
    assert.throws(() => validateLock(manifest, Buffer.from(JSON.stringify(lock)), version, sri), /origin/);
    entry.link = true;
    assert.throws(() => validateLock(manifest, Buffer.from(JSON.stringify(lock)), version, sri), /path/);
    const scripted = { ...JSON.parse(manifest), scripts: { install: 'bad' } };
    assert.throws(() => validateLock(Buffer.from(JSON.stringify(scripted)), Buffer.from(JSON.stringify(lock)), version, sri), /manifest/);
});

test('existing selected entry is reused byte-for-byte; linked input is refused', () => {
    const f = fixture();
    const catalog = JSON.parse(fs.readFileSync(path.join(f.root, 'tools/dsh-locks/catalog.json')));
    const current = JSON.parse(fs.readFileSync(path.join(f.root, 'tools/dsh-seed/package.json'))).dependencies['@deepseek-ai/dsh'];
    const entry = catalog.entries.find(e => e.version === current);
    f.selection.version = current;
    f.selection.integrity = entry.dshSri;
    for (const name of ['package.json', 'package-lock.json']) {
        fs.copyFileSync(path.join(f.root, 'tools/dsh-seed', name), path.join(f.input, name));
    }
    fs.writeFileSync(path.join(f.input, 'catalog.json'), JSON.stringify(catalog));
    const link = `${f.input}-link`;
    fs.symlinkSync(f.input, link, process.platform === 'win32' ? 'junction' : 'dir');
    assert.throws(() => prepareCandidate(f.root, link, f.selection, f.output), /Linked/);
    const report = prepareCandidate(f.root, f.input, f.selection, f.output);
    assert.deepEqual(report.changedFiles, []);
});

test('catalog pruning removes directories without deleting historical seed authorization', () => {
    const f = fixture(true);
    const before = JSON.parse(fs.readFileSync(path.join(f.root, 'tools/dsh-locks/catalog.json')));
    const oldest = before.entries.at(-1).version;
    prepareCandidate(f.root, f.input, f.selection, f.output);
    assert.equal(fs.existsSync(path.join(f.root, 'tools/dsh-locks/locks', oldest)), false);
    assert.equal(fs.readdirSync(path.join(f.root, 'tools/dsh-locks/locks')).length, 5);
    assert.ok(fs.readFileSync(path.join(f.root, 'Services/DshWebRuntime.cs'), 'utf8').includes(`"${oldest}"`));
});
