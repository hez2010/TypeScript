import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import {root, output, json, sha256, referenceRevision} from './common.mjs';

const dotnet = process.argv.includes('--dotnet') ? process.argv[process.argv.indexOf('--dotnet') + 1] : 'dotnet';
const directory = path.join(output, 'declarations');
const results = JSON.parse(await readFile(path.join(directory, 'results.json'), 'utf8'));
const oracle = path.join(output, 'program-emit/oracle.exe');
const managedDirectory = process.argv.includes('--managed-directory') ? process.argv[process.argv.indexOf('--managed-directory') + 1]
    : path.join(root, 'csharp/tests/TypeScript.Compatibility/bin/Release/net11.0');
const candidate = path.join(managedDirectory, 'TypeScript.Compatibility.dll');
const selected = results.filter(item => item.input.name.includes('expando-alias-after-variable') || item.input.name.includes('this-negative-key') || item.input.name.includes('this-computed-name'));
const controls = [];
for (const item of selected) {
    const expando = item.input.name.includes('expando-');
    const consumer = expando ? 'import {f} from "./input"; const first: string = f.first; const value: number = f.value;'
        : item.input.name.includes('negative') ? 'import {C} from "./input"; const value: number = new C()[-1];'
            : 'import {C} from "./input"; const value: number = new C().key; const other: number = new C().value;';
    for (const [version, text, extension] of [['original', item.input.text, item.input.file?.endsWith('.js') ? '.js' : '.ts'],
        ['candidate', Buffer.from(item.candidate.textBase64, 'base64').toString('utf8'), '.d.ts'],
        ...(!item.reference.error ? [['reference', Buffer.from(item.reference.textBase64, 'base64').toString('utf8'), '.d.ts']] : [])])
        controls.push({name: `${item.input.name}-${version}`, file: '/source/consumer.ts', text: consumer, checkOnly: '/source/consumer.ts',
            files: {[`/source/input${extension}`]: text}, options: {allowJs: true, target: 'esnext'}});
}
async function execute(command, args) {
    const child = spawn(command, args, {cwd: root, windowsHide: true});
    const out = [], err = [];
    child.stdout.on('data', bytes => out.push(bytes)); child.stderr.on('data', bytes => err.push(bytes));
    child.stdin.end(controls.map(input => JSON.stringify(input)).join('\n') + '\n');
    const code = await new Promise((resolve, reject) => {child.on('error', reject); child.on('close', resolve);});
    assert.equal(code, 0, Buffer.concat(err).toString('utf8'));
    return Buffer.concat(out).toString('utf8').trim().split('\n').map(line => JSON.parse(line));
}
const [reference, actual] = await Promise.all([execute(oracle, []), execute(dotnet, [candidate, '--program-emit-lines'])]);
const evidence = controls.map((input, index) => ({input, reference: reference[index], candidate: actual[index]}));
await json(path.join(directory, 'consumer-results.json'), evidence);
for (const item of evidence) {
    assert.deepEqual(item.candidate, item.reference, item.input.name);
    const expected = item.input.name.includes('expando-alias-after-variable') && item.input.name.endsWith('-reference') ? [2339] : [];
    assert.deepEqual(item.reference.diagnostics.map(diagnostic => diagnostic.code), expected, item.input.name);
}
const summary = {referenceRevision, checks: evidence.length, failed: 0, inputHash: sha256(JSON.stringify(controls)),
    resultHash: sha256(JSON.stringify(evidence)), oracleHash: sha256(await readFile(oracle)), candidateHash: sha256(await readFile(candidate)),
    compilerHash: sha256(await readFile(path.join(path.dirname(candidate), 'TypeScript.Compiler.dll')))};
await json(path.join(directory, 'consumer-summary.json'), summary);
if (process.argv.includes('--record')) await json(path.join(root, 'csharp/compatibility/evidence/phase5-declaration-consumers.json'), {...summary, evidence});
console.log(JSON.stringify(summary, null, 2));
