import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
} from "node:fs/promises";
import path from "node:path";
import { gzipSync } from "node:zlib";
import {
    json,
    output,
    referenceRevision,
    root,
    run,
    sha256,
} from "./common.mjs";
import {
    bindingHash,
    classifyBindingDifference,
    parserBaseline,
} from "./compare-binding.mjs";
const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(output, "binding-oracle.exe");
const managed = process.argv.includes("--managed");
const native = path.join(output, "phase3-native");
const candidate = managed ? dotnet : path.join(native, "TypeScript.Compatibility.exe");
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const args = [...managed ? [dll] : [], "--binding-lines"];
await mkdir(path.join(source, "cmd/binding-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/binding/main.go"), path.join(source, "cmd/binding-probe/main.go"));
await copyFile(path.join(root, "csharp/oracle/binding/syntax.generated.go"), path.join(source, "internal/ast/csharp_binding_syntax.generated.go"));
await copyFile(path.join(root, "csharp/oracle/syntax/scalars.generated.go"), path.join(source, "internal/ast/csharp_scalars.generated.go"));
await mkdir(path.join(source, "cmd/syntax-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/syntax/main.go"), path.join(source, "cmd/syntax-probe/main.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", path.join(output, "syntax-oracle.exe"), "./cmd/syntax-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/binding-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) {
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", native],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}
const texts = [
    "",
    "let x = 1; x++;",
    "var x; var x;",
    "let x; let x;",
    "let x; var x;",
    "var x; let x;",
    "function f(x,x) {}",
    "export const x=1; export interface I {x:string} export class C {}",
    "export default function f(){} export default class C {}",
    "function f<T>(x:T):T { let y=x; return y; }",
    "function f(){ function g() {} var x; }",
    "interface A {x:string; f(x:number):void} interface A {y:number; f(x:string):void}",
    "class C { static x=1; #p=2; x?:number; constructor(public y:string){} get p(){return this.#p} set p(x){this.#p=x} }",
    "namespace N { export const x=1; export namespace M { export type T=number } } namespace N { export function f(){} }",
    "namespace N { export const enum E { A,B } } namespace N { export interface I{} }",
    "declare module 'pkg' { const x: number; interface I{} }",
    "declare module 'pkg' { export { x }; const x: number; }",
    "export {}; declare global { interface Array<T>{custom:T} }",
    "enum E { A,B } enum E { C=1 }",
    "enum E{} class E{}",
    "import x, { A as B } from 'p'; import * as ns from 'q'; import a = require('a'); export { x }; export * from 'r'; export * as m from 's';",
    "export = value;",
    "export as namespace Lib; export const x:number;",
    "type F = <T>(x:T)=>T; type C=new()=>object;",
    "type T<X> = X extends infer U ? U : never; type M<X> = { [K in keyof X]?: X[K] };",
    "const {a,b:[c=1,...d]}=value; function f({x,y=x}={x:1,y:1}){return y}",
    "function f(x){ if(x){x=1;}else{x=2;} return x; }",
    "function f(x){while(x){x--; if(x)break;continue;} return x}",
    "function f(x){do{x--;}while(x);return x}",
    "for(let i=0;i<10;i++){if(i)continue;break;}",
    "for(const x of values){x;} for(let k in obj){k;}",
    "outer: while(x){if(y)continue outer; break outer;}",
    "function f(x){try{x=1;return x;}catch(e){x=2;}finally{x=3;}return x;}",
    "switch(x){case 1: case 2: y=1;break;default:y=2;case 3:y=3;}",
    "x && (y=1); x || (y=2); x ?? (y=3); x &&= y; x ||= y; x ??= y;",
    "if(x && y || !z){x;} x ? y++ : z++;",
    "x?.y?.(a=1); if(x?.[y++]){x;}",
    "(() => {x=1; return x;})(); (function(){throw 1;})(); let y=2;",
    "for((()=>{throw 1;})();x;y++){z;} for(let a of (()=>{throw 1;})()){a;}",
    "a.push(1); a.unshift(2); a[i]=3; delete a.x; assert(x); fn(x), other(y);",
    "[a,b=1,...c]=x; ({a,b:c=1,...d}=x);",
    "const f=()=>1; f.x=2; function g(){} g.x=1; const C=class {}; C.x=2;",
    "class C { static { this.x=1; } x=()=>this; }",
    "interface I { this:this; }",
    "let eval; function arguments(eval) { arguments++; delete eval; } with(x){}",
    "let implements; function f(await){}",
    "const o={ get x(){return 1},set x(v){},x:1 };",
    "const o={ ['a']:1, [1]:2, [foo]:3 };",
    "module.exports = function(){}; exports.x=1; const y=require('y'); Object.defineProperty(exports,'z',{value:1});",
    "class C { constructor(){this.x=1;} x(){} }",
    "/** @typedef {string} T */ const x=1; /** @type {T} */ let y;",
    "exports.f=function(){}; /** @typedef {number} T */ let x; module.exports=exports.f;",
];
let cases = [];
for (let i = 0; i < texts.length; i++) for (const extension of ["ts", "js", "d.ts"]) cases.push({ name: `focused:${i}:${extension}`, fileName: `/project/test.${extension}`, text: Buffer.from(texts[i]).toString("base64") });
async function probe(command, args, items) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const out = [], errors = [];
        child.stdout.on("data", b => out.push(b));
        child.stderr.on("data", b => errors.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            const lines = Buffer.concat(out).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code) reject(new Error(`${command} exited ${code}; next ${items[lines.length]?.name}: ${Buffer.concat(errors)}`));
            else resolve(lines.map(x => JSON.parse(x)));
        });
        child.stdin.end(items.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
if (process.argv.includes("--corpus")) {
    const files = (await run("git", ["ls-files", "-z", "tsc/testdata/tests/cases", "tsc/internal/bundled/libs"])).split("\0").filter(f => /\.(ts|tsx|js|jsx)$/.test(f));
    const requests = files.map(f => ({ name: f, path: path.join(root, f), mode: "units", details: true }));
    const expanded = await probe(path.join(output, "syntax-oracle.exe"), [], requests);
    for (let i = 0; i < expanded.length; i++) {
        for (const [name, content] of expanded[i].details[0] ?? []) {
            const fileName = Buffer.from(name, "base64").toString();
            if (/\.(ts|tsx|js|jsx)$/.test(fileName)) cases.push({ name: files[i] + ":" + fileName, fileName: path.posix.resolve("/project", fileName.replaceAll("\\", "/")), text: content });
        }
    }
}
if (option("--filter")) cases = cases.filter(c => c.name.includes(option("--filter")));
if (option("--inputs")) cases = JSON.parse(await readFile(option("--inputs"), "utf8"));
await json(path.join(output, "binding-inputs.json"), cases);
const failures = [];
let passed = 0;
for (let offset = 0; offset < cases.length; offset += 50) {
    const batch = cases.slice(offset, offset + 50);
    const expected = await probe(oracle, [], batch), actual = await probe(candidate, args, batch);
    assert.equal(expected.length, batch.length);
    assert.equal(actual.length, batch.length);
    for (let i = 0; i < batch.length; i++) {
        try {
            assert.deepEqual(actual[i], expected[i]);
            passed++;
        }
        catch {
            failures.push({ input: batch[i], expected: expected[i], actual: actual[i] });
        }
    }
    if (cases.length > 500) console.log(`${offset + batch.length}/${cases.length}, ${failures.length} differences`);
}
const permitted = [];
if (failures.length) {
    const rebound = [];
    for (let offset = 0; offset < failures.length; offset += 25) {
        const batch = failures.slice(offset, offset + 25);
        const actual = await probe(candidate, args, batch.map(f => ({ ...f.input, exportTree: true })));
        const expected = await probe(oracle, [], batch.map((f, i) => ({ ...f.input, tree: actual[i].tree })));
        const syntaxRequests = batch.map(f => ({ ...f.input, name: parserBaseline(f.input)?.name ?? f.input.name, fileName: parserBaseline(f.input)?.fileName ?? f.input.fileName, mode: "parse" }));
        const syntaxExpected = await probe(path.join(output, "syntax-oracle.exe"), [], syntaxRequests);
        const syntaxActual = await probe(candidate, [...managed ? [dll] : [], "--scan-lines"], syntaxRequests);
        for (let i = 0; i < batch.length; i++) {
            const f = batch[i];
            const syntax = { expectedHash: syntaxExpected[i].hash, actualHash: syntaxActual[i].hash };
            const policy = classifyBindingDifference(f.input, f.actual, actual[i], expected[i], syntax);
            if (policy) permitted.push({ ...f, policy, syntax, candidateTree: actual[i].tree, syntaxFingerprint: actual[i].syntaxFingerprint, rebound: expected[i].binding });
            try {
                assert.deepEqual(actual[i].binding, expected[i].binding);
                assert.equal(actual[i].syntaxFingerprint, expected[i].syntaxFingerprint);
                rebound.push({ input: f.input, matches: true });
            }
            catch {
                rebound.push({ input: f.input, matches: false, expected: expected[i], actual: actual[i] });
            }
        }
    }
    await json(path.join(output, "binding-candidate-tree.json"), rebound);
    console.log("Same candidate syntax tree:", rebound.filter(r => r.matches).length, "/", rebound.length);
}
await json(path.join(output, "binding-failures.json"), failures);
const unresolved = failures.filter(f => !permitted.some(p => p.input.name === f.input.name));
await json(path.join(output, "binding-unexplained.json"), unresolved);
const summary = { timestamp: new Date().toISOString(), referenceRevision, managed, cases: cases.length, passed: passed + permitted.length, exact: passed, strictDifferences: failures.length, permittedDifferences: permitted.length, failed: unresolved.length, inputSha256: sha256(JSON.stringify(cases)), oracleSha256: sha256(await readFile(oracle)), candidateSha256: sha256(await readFile(managed ? dll : candidate)) };
await json(path.join(output, "binding-summary.json"), summary);
console.log(summary);
if (option("--record")) {
    const record = option("--record");
    const archive = gzipSync(JSON.stringify({ referenceRevision, differences: permitted }));
    const { writeFile } = await import("node:fs/promises");
    await writeFile(path.join(root, `csharp/compatibility/evidence/${record}-differences.outputs.json.gz`), archive);
    await json(path.join(root, `csharp/compatibility/evidence/${record}-differences.json`), {
        referenceRevision,
        outputsFile: `${record}-differences.outputs.json.gz`,
        outputsSha256: sha256(archive),
        rationale: "Live syntax hashes match the reviewed phase-2 ledger; the pinned Go binder on the complete candidate syntax tree exactly matches C# binding, including scopes, symbols, declarations, flow edges and diagnostics.",
        reproduction: "node csharp/tools/binding.mjs --corpus --strict",
        cases: permitted.map(f => ({ name: f.input.name, fileName: f.input.fileName, sourceSha256: sha256(Buffer.from(f.input.text, "base64")), policy: f.policy, expectedSha256: bindingHash(f.expected), actualSha256: bindingHash(f.actual), candidateTreeSha256: bindingHash(f.candidateTree), reboundSha256: bindingHash(f.rebound), syntax: f.syntax })),
    });
    await json(path.join(root, `csharp/compatibility/evidence/${record}.json`), summary);
}
if (unresolved.length || process.argv.includes("--strict") && failures.length) {
    for (const f of unresolved.slice(0, 12)) console.log(f.input.name, f.actual.map((a, i) => JSON.stringify(a) === JSON.stringify(f.expected[i])));
    process.exitCode = 1;
}
