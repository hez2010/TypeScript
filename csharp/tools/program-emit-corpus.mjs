import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {copyFile, mkdir, readFile} from 'node:fs/promises';
import path from 'node:path';
import {root, output, run, json, sha256, referenceRevision} from './common.mjs';
import {programEmitCases} from './program-emit-cases.mjs';

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option('--dotnet', 'dotnet');
const go = option('--go', 'D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe');
const directory = path.join(output, 'program-emit');
await mkdir(directory, {recursive: true});
const reference = JSON.parse(await readFile(path.join(output, 'reference.json'), 'utf8'));
const source = path.join(output, reference.sourceRelativePath, 'tsc');
const bridge = path.join(root, 'csharp/oracle/program-emit/main.go');
const oracle = path.join(directory, 'oracle.exe');
const candidate = path.join(option('--managed-directory', path.join(root, 'csharp/tests/TypeScript.Compatibility/bin/Release/net11.0')), 'TypeScript.Compatibility.dll');
if (!process.argv.includes('--no-build')) {
    const bridgeDirectory = path.join(source, 'cmd/csharp-program-emit');
    await mkdir(bridgeDirectory, {recursive: true});
    await copyFile(bridge, path.join(bridgeDirectory, 'main.go'));
    await run(go, ['-C', source, 'build', '-o', oracle, './cmd/csharp-program-emit'], {env: {...process.env, GOWORK: 'off', GOTOOLCHAIN: 'local'}});
    await run(dotnet, ['build', 'csharp/tests/TypeScript.Compatibility/TypeScript.Compatibility.csproj', '-c', 'Release', '--no-restore']);
}
const cases = programEmitCases().filter(item => item.name.includes(option('--filter', '')));
await json(path.join(directory, 'inputs.json'), cases);
async function execute(command, args) {
    const child = spawn(command, args, {cwd: root, windowsHide: true});
    const output = [], errors = [];
    child.stdout.on('data', bytes => output.push(bytes));
    child.stderr.on('data', bytes => errors.push(bytes));
    child.stdin.on('error', () => {});
    child.stdin.end(cases.map(item => JSON.stringify(item)).join('\n') + '\n');
    const code = await new Promise((resolve, reject) => { child.on('error', reject); child.on('close', resolve); });
    const stdout = Buffer.concat(output).toString('utf8'), stderr = Buffer.concat(errors).toString('utf8');
    if (code) throw Error(`${command} exited ${code}\n${stderr}\n${stdout.slice(-1000)}`);
    return stdout.trim().split('\n').map(line => JSON.parse(line));
}
const [expected, actual] = await Promise.all([execute(oracle, []), execute(dotnet, [candidate, '--program-emit-lines'])]);
assert.equal(expected.length, cases.length); assert.equal(actual.length, cases.length);
const results = cases.map((input, index) => ({input, reference: expected[index], candidate: actual[index]}));
const failures = results.filter(item => {
    if (item.reference.error || item.candidate.error) return true;
    try { assert.deepEqual(item.candidate, item.reference); return false; } catch { return true; }
});
await json(path.join(directory, 'results.json'), results);
await json(path.join(directory, 'failures.json'), failures);
const summary = {referenceRevision, cases: cases.length, strictPass: cases.length - failures.length, failed: failures.length,
    fixtureHash: sha256(JSON.stringify(cases)), bridgeHash: sha256(await readFile(bridge)),
    candidateHash: sha256(await readFile(candidate)), compilerHash: sha256(await readFile(path.join(path.dirname(candidate), 'TypeScript.Compiler.dll'))),
    oracleHash: sha256(await readFile(oracle)), resultHash: sha256(JSON.stringify(results))};
await json(path.join(directory, 'summary.json'), summary);
console.log(JSON.stringify(summary, null, 2));
for (const item of failures.slice(0, 12)) console.log(item.input.name, item.reference.error ?? '', item.candidate.error ?? '');
if (process.argv.includes('--record')) {
    assert.equal(failures.length, 0, 'Program emit differences must be resolved before recording');
    await json(path.join(root, 'csharp/compatibility/evidence/phase5-program-emit.json'), summary);
}
if (failures.length) process.exitCode = 1;
