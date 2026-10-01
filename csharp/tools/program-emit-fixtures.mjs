import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createReadStream} from 'node:fs';
import {copyFile, mkdir, readFile} from 'node:fs/promises';
import {createInterface} from 'node:readline';
import path from 'node:path';
import {root, output, json, run, sha256, referenceRevision} from './common.mjs';
import {fixtureCorrectionProbe} from './program-emit-fixture-probes.mjs';

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option('--dotnet', 'dotnet');
const go = option('--go', 'D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe');
const tag = option('--tag', 'current');
if (!/^[a-z0-9-]+$/.test(tag)) throw Error('Invalid fixture tag');
const directory = path.join(output, `program-emit-fixtures-${tag}`);
const inputsDirectory = option('--reference-directory', path.join(output, 'semantic-corpus-preemit-baseline/single'));
await mkdir(directory, {recursive: true});
const oracle = path.join(output, 'program-emit/oracle.exe');
const candidate = path.join(option('--managed-directory', path.join(root, 'csharp/tests/TypeScript.Compatibility/bin/Release/net11.0')), 'TypeScript.Compatibility.dll');
const reference = JSON.parse(await readFile(path.join(output, 'reference.json'), 'utf8'));
const source = path.join(output, reference.sourceRelativePath, 'tsc');
const bridge = path.join(root, 'csharp/oracle/program-emit/main.go');
if (!process.argv.includes('--no-build')) {
    const bridgeDirectory = path.join(source, 'cmd/csharp-program-emit');
    await mkdir(bridgeDirectory, {recursive: true});
    await copyFile(bridge, path.join(bridgeDirectory, 'main.go'));
    await run(go, ['-C', source, 'build', '-o', oracle, './cmd/csharp-program-emit'], {env: {...process.env, GOWORK: 'off', GOTOOLCHAIN: 'local'}});
    await run(dotnet, ['build', 'csharp/tests/TypeScript.Compatibility/TypeScript.Compatibility.csproj', '-c', 'Release', '--no-restore']);
}
const filter = new RegExp(option('--filter', 'declarationEmit|declarationMap|jsDeclaration|isolatedDeclaration'), 'i');
const declarationOptions = process.argv.includes('--declarations');
const retryTag = option('--retry', '');
if (retryTag && !/^[a-z0-9-]+$/.test(retryTag)) throw Error('Invalid retry tag');
const retryCases = retryTag ? JSON.parse(await readFile(path.join(output, `program-emit-fixtures-${retryTag}`, 'failures.json'), 'utf8')) : null;
const retryNames = retryCases ? new Set(retryCases.map(item => item.input.name)) : null;
const planned = [], cases = [];
const blobs = new Map();
for await (const line of createInterface({input: createReadStream(path.join(inputsDirectory, 'cases.jsonl')), crlfDelay: Infinity})) {
    const row = JSON.parse(line);
    if (retryNames ? !retryNames.has(row.name) : declarationOptions ? !row.options?.declaration && !row.options?.composite : !filter.test(row.name)) continue;
    planned.push({name: row.name, source: row.source, status: row.status, contentMappers: row.contentMappers ?? 0});
    if (row.status !== 'ready' || cases.length >= Number(option('--limit', Infinity))) continue;
    const files = {};
    for (const [file, hash] of Object.entries(row.files)) {
        if (!blobs.has(hash)) {
            const bytes = await readFile(path.join(inputsDirectory, 'blobs', hash));
            assert.equal(sha256(bytes), hash);
            blobs.set(hash, bytes.toString('utf8'));
        }
        files[file] = blobs.get(hash);
    }
    cases.push({name: row.name, source: row.source, corpus: true, cwd: row.currentDirectory,
        files, symlinks: row.symlinks, configFileName: row.configFileName, roots: row.roots, options: row.options,
        caseInsensitive: !row.caseSensitive, contentMappers: row.contentMappers ?? 0});
}
const mapperFixture = path.join(output, 'content-mapper-fixture.exe');
let mapperFixtureInfo;
if (cases.some(input => input.contentMappers)) {
    const fixtureSource = path.join(root, 'csharp/oracle/content-mapper-fixture/main.go');
    const sourceSha256 = sha256(await readFile(fixtureSource));
    try {
        const cached = JSON.parse(await readFile(path.join(output, 'content-mapper-fixture.json'), 'utf8'));
        if (cached.referenceRevision === referenceRevision && cached.sourceSha256 === sourceSha256 && cached.go === go
            && cached.executableSha256 === sha256(await readFile(mapperFixture))) mapperFixtureInfo = cached;
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (!mapperFixtureInfo) {
        const fixtureDirectory = path.join(source, 'cmd/csharp-content-mapper-fixture');
        await mkdir(fixtureDirectory, {recursive: true});
        await copyFile(fixtureSource, path.join(fixtureDirectory, 'main.go'));
        await run(go, ['-C', source, 'build', '-o', mapperFixture, './cmd/csharp-content-mapper-fixture'],
            {env: {...process.env, GOWORK: 'off', GOTOOLCHAIN: 'local'}});
        mapperFixtureInfo = {referenceRevision, sourceSha256, go, executableSha256: sha256(await readFile(mapperFixture))};
        await json(path.join(output, 'content-mapper-fixture.json'), mapperFixtureInfo);
    }
}
await json(path.join(directory, 'plan.json'), planned);
await json(path.join(directory, 'inputs.json'), cases);
async function execute(command, args, label) {
    const child = spawn(command, args, {cwd: root, windowsHide: true,
        env: {...process.env, CSHARP_CONTENT_MAPPER_FIXTURE: mapperFixtureInfo ? mapperFixture : ''}});
    const results = [], errors = [];
    child.stderr.on('data', bytes => errors.push(bytes)); child.stdin.on('error', () => {});
    const read = (async () => {
        for await (const line of createInterface({input: child.stdout, crlfDelay: Infinity})) {
            results.push(JSON.parse(line));
            if (results.length % 25 === 0) console.log(`${label}: ${results.length}/${cases.length}`);
        }
    })();
    const exit = new Promise((resolve, reject) => { child.on('error', reject); child.on('close', resolve); });
    for (const input of cases) if (!child.stdin.write(JSON.stringify(input) + '\n'))
        await new Promise(resolve => {
            const done = () => { child.stdin.off('drain', done); child.stdin.off('error', done); resolve(); };
            child.stdin.once('drain', done); child.stdin.once('error', done);
        });
    child.stdin.end();
    const code = await exit; await read;
    if (code) throw Error(`${label} exited ${code}\n${Buffer.concat(errors).toString('utf8')}`);
    assert.equal(results.length, cases.length, `${label} result count`);
    return results;
}
async function binaryHashes() {
    return {candidateHash: sha256(await readFile(candidate)),
        compilerHash: sha256(await readFile(path.join(path.dirname(candidate), 'TypeScript.Compiler.dll'))),
        bridgeHash: sha256(await readFile(bridge)), oracleHash: sha256(await readFile(oracle))};
}
const binaries = await binaryHashes();
const [expected, actual] = await Promise.all([execute(oracle, [], 'reference'), execute(dotnet, [candidate, '--program-emit-lines'], 'candidate')]);
const results = cases.map((input, index) => ({input, reference: expected[index], candidate: actual[index]}));
const stable = value => Array.isArray(value) ? value.map(stable) : value && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, stable(value[key])])) : value;
const hash = value => sha256(JSON.stringify(stable(value)));
const corrections = JSON.parse(await readFile(path.join(root, 'csharp/compatibility/emit-fixture-corrections.json'), 'utf8'));
assert.equal(corrections.referenceRevision, referenceRevision);
const permitted = [];
const failures = results.filter(item => {
    if (item.reference.error || item.candidate.error) return true;
    if (hash(item.candidate) === hash(item.reference)) return false;
    const correction = corrections.entries.find(entry => entry.name === item.input.name);
    if (correction && correction.inputHash === hash({...item.input, source: undefined})
        && correction.referenceHash === hash(item.reference) && correction.candidateHash === hash(item.candidate)) {
        permitted.push({...item, policy: correction.policy, runtime: fixtureCorrectionProbe(item, correction.policy)});
        return false;
    }
    return true;
});
await json(path.join(directory, 'results.json'), results); await json(path.join(directory, 'failures.json'), failures);
assert.deepEqual(await binaryHashes(), binaries, 'Emission binaries changed during the comparison');
const summary = {referenceRevision, scope: retryNames ? `failed cases from ${retryTag}` : declarationOptions ? 'compilerOptions.declaration || compilerOptions.composite' : filter.source,
    planned: planned.length, referenceSkipped: planned.filter(item => item.status !== 'ready').length,
    mappedCases: cases.filter(item => item.contentMappers).length,
    cases: cases.length, strictPass: cases.length - failures.length - permitted.length,
    permittedDifferences: permitted.length, failed: failures.length,
    fixtureHash: sha256(JSON.stringify(cases)), planHash: sha256(JSON.stringify(planned)),
    ...binaries, resultHash: sha256(JSON.stringify(results)),
    ...mapperFixtureInfo ? {mapperFixture: mapperFixtureInfo} : {},
    differences: permitted};
await json(path.join(directory, 'summary.json'), summary); console.log(JSON.stringify(summary, null, 2));
for (const item of failures.slice(0, 30)) console.log(item.input.name, item.reference.error ?? '', item.candidate.error ?? '');
if (process.argv.includes('--record')) {
    assert.equal(failures.length, 0, 'Compiler fixture differences must be resolved before recording');
    await json(path.join(root, 'csharp/compatibility/evidence/phase5-emit-fixtures.json'), summary);
}
if (failures.length) process.exitCode = 1;
