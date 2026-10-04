import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { gzipSync } from "node:zlib";
import ts from "typescript";
import {
    json,
    output,
    referenceRevision,
    root,
    run,
    sha256,
} from "./common.mjs";
import { parserBaseline } from "./compare-binding.mjs";
import { classifyNameDifference } from "./compare-checker-names.mjs";
const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc"), oracle = path.join(output, "checker-names-oracle.exe");
const managed = process.argv.includes("--managed"), native = path.join(output, "phase4-native");
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const candidate = managed ? dotnet : path.join(native, "TypeScript.Compatibility.exe");
await mkdir(path.join(source, "cmd/checker-names-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/checker-names/main.go"), path.join(source, "cmd/checker-names-probe/main.go"));
await copyFile(path.join(root, "csharp/oracle/binding/syntax.generated.go"), path.join(source, "internal/ast/csharp_binding_syntax.generated.go"));
await copyFile(path.join(root, "csharp/oracle/syntax/scalars.generated.go"), path.join(source, "internal/ast/csharp_scalars.generated.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/checker-names-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) {
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-p:PublishAot=true", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-o", native],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}

const texts = [
    "let x=1; function f(){ let x=2; return x; } x; missing;",
    "type T=number; function f<T>(x:T):T {type U=T;var y:T;return x;} U;T;",
    "let x=1; function f<T extends typeof x>(a=x,b=y):typeof a {var x=2,y=3;type T=string;return a;} arguments;",
    "function f<T extends U,U extends T>(a:T,b=()=>a):U {return a;} T; U;",
    "type F = <T>(x:T)=>T; type C=new<T>()=>T; interface I{<T>(x:T):T;new<U>():U;f<T>(x:T):T}",
    "class C<T> { x:T; static x:T; [f<T>()]():T {return this.x} constructor(a:T){} }",
    "interface I<T> { [f<T>()]:T } class C<T> extends T<T> {}",
    "const C=class Named<T> extends Base<T> { x:Named<T>; f(){return Named;} }; Named;",
    "const f=function named<T>(a=named):T {named;arguments;return a}; named;",
    "function f(){return ()=>arguments;} const g=()=>arguments; (function(){arguments;})();",
    "let x=0; class C { x=x; y=a; constructor(a:number){var x=1;} }",
    "let x=0; function f(a=x){var x=1;} function g({a=x}={}){var x=1;}",
    "let x=0; function f(a=class {static y=x}){var x=1;}",
    "let x=0; function f(a=x?.p){var x=1;} function g(a=x??1){var x=1;}",
    "let x=0; function f({a,...rest}=x){var x=1;} function g(a=()=>x){var x=1;}",
    "let x=0; function f(a={get [x](){return x}, f(){return x}}){var x=1;}",
    "type T<X> = X extends infer U ? U : U; type V<X> = X extends (infer U extends string) ? U : never;",
    "type M<X> = { [K in keyof X as K]: X[K] }; K;",
    "namespace N { export const N=1; N; export interface I{}; I; } N;",
    "namespace N { export const x=1; export function f(){x;} export class C{}; C; } N;",
    "enum E { E=1,A=E,B=A } E; namespace N {export enum E{A};E;}",
    "export default function named(){named;} named; default;",
    "export default class Named { x:Named; } Named;",
    "const x=1; export {x as y}; x;y; export {remote as local} from 'p'; local; remote;",
    "export * as ns from 'p'; ns; import x,{a as b,type C} from 'p';x;b;C;",
    "import type {I} from 'p'; import type * as ns from 'p'; import X=require('q');I;ns;X;",
    "declare module 'p' { function f():void; interface I{}; export {f as g}; f;g;I; }",
    "export {};declare global {interface Window{custom:number}};Window;",
    "let x=1; let a=x as const; let b=<const>x; type const=number;const;",
    "function d(){} class C<T>{@d method(@d p:T,d:any){return d;} }",
    "type T=number; @dec(1 as T) class C<T>{@dec(1 as T) p:T;}",
    "function f(a={x:1}, {x=a.x,y=x}=a){return y;} const z=()=>f;",
    "function f(){f();} class C{x:C} interface I{x:I} type T=T[];enum E{A};namespace N{N;}",
    "let x; (()=>x)(); (async()=>x)(); (function*(){return x})(); let f=()=>x; type T=typeof x;",
    "module.exports=x;exports.f=f; function f(){exports.f;} require('p'); const p=require('p');p;",
    "const {a}=require('p');a;const n=require('n').x;n; module['exports']=f;exports['f']=f; function f(){}",
    "/** @template T @param {T} x @returns {T} */ function f(x){return x;} T;",
    "function f(){try{throw 1}catch(e){e;let x=e;}e;} for(let x of xs){x;}x;",
    "export class C<T>{constructor(public x:T){} get value():T{return this.x} set value(x:T){this.x=x}}",
    "class C { static {let x=1;x;} } x;",
    "let x=0; class C { y=x; z=a; constructor(a:number); constructor(a:any){var x=1;} }",
    "let x;function f(a=class extends (x?.C) {}){var x;}",
];
let cases = [];
for (let i = 0; i < texts.length; i++) for (const extension of ["ts", "js", "d.ts"]) for (const target of ["es5", "es2015", "es2017", "es2020", "es2022", "esnext"]) for (const excludeGlobals of [false, true]) cases.push({ name: `focused:${i}:${extension}:${target}:${excludeGlobals}`, fileName: `/project/test.${extension}`, text: Buffer.from(texts[i]).toString("base64"), options: { target, useDefineForClassFields: i % 2 === 0 }, excludeGlobals });

async function probe(command, args, items) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true }), stdout = [], stderr = [];
        child.stdout.on("data", b => stdout.push(b));
        child.stderr.on("data", b => stderr.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            const lines = Buffer.concat(stdout).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code) reject(Error(`${command}: ${items[lines.length]?.name}: ${Buffer.concat(stderr)}`));
            else resolve(lines.map(JSON.parse));
        });
        child.stdin.end(items.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
async function prepareSyntaxOracle() {
    await mkdir(path.join(source, "cmd/syntax-probe"), { recursive: true });
    await copyFile(path.join(root, "csharp/oracle/syntax/main.go"), path.join(source, "cmd/syntax-probe/main.go"));
    await copyFile(path.join(root, "csharp/oracle/syntax/scalars.generated.go"), path.join(source, "internal/ast/csharp_scalars.generated.go"));
    const syntaxOracle = path.join(output, "syntax-oracle.exe");
    await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", syntaxOracle, "./cmd/syntax-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    return syntaxOracle;
}
if (process.argv.includes("--corpus")) {
    const syntaxOracle = await prepareSyntaxOracle();
    const files = (await run("git", ["ls-files", "-z", "tsc/testdata/tests/cases", "tsc/internal/bundled/libs"])).split("\0").filter(f => /\.(ts|tsx|js|jsx)$/.test(f));
    const expanded = await probe(syntaxOracle, [], files.map(f => ({ name: f, path: path.join(root, f), mode: "units", details: true })));
    for (let i = 0; i < expanded.length; i++) {
        for (const [name, content] of expanded[i].details[0] ?? []) {
            const fileName = Buffer.from(name, "base64").toString();
            if (/\.(ts|tsx|js|jsx)$/.test(fileName)) cases.push({ name: files[i] + ":" + fileName, fileName: path.posix.resolve("/project", fileName.replaceAll("\\", "/")), text: content });
        }
    }
}
if (option("--inputs")) cases = JSON.parse(await readFile(option("--inputs"), "utf8"));
if (option("--filter")) cases = cases.filter(c => c.name.includes(option("--filter")));
const failures = [];
let queries = 0, references = 0;
for (let start = 0; start < cases.length; start += 50) {
    const batch = cases.slice(start, start + 50);
    const expected = await probe(oracle, [], batch), actual = await probe(candidate, [...managed ? [dll] : [], "--checker-names-lines"], batch);
    assert.equal(actual.length, batch.length);
    assert.equal(expected.length, batch.length);
    for (let i = 0; i < batch.length; i++) {
        queries += actual[i][0].length;
        references += actual[i][2].length;
        try {
            assert.deepEqual(actual[i], expected[i]);
        }
        catch {
            failures.push({ input: batch[i], expected: expected[i], actual: actual[i] });
        }
    }
    if ((start + batch.length) % 500 === 0) console.log(`${start + batch.length}/${cases.length}, ${failures.length} differences`);
}
await json(path.join(output, "checker-names-failures.json"), failures);
const permitted = [];
if (failures.length) {
    const syntaxOracle = await prepareSyntaxOracle();
    for (let start = 0; start < failures.length; start += 25) {
        const batch = failures.slice(start, start + 25);
        const exported = await probe(candidate, [...managed ? [dll] : [], "--checker-names-lines"], batch.map(f => ({ ...f.input, exportTree: true })));
        const rebound = await probe(oracle, [], batch.map((f, i) => ({ ...f.input, tree: exported[i].tree })));
        const requests = batch.map(f => ({ ...f.input, name: parserBaseline(f.input)?.name ?? f.input.name, fileName: parserBaseline(f.input)?.fileName ?? f.input.fileName, mode: "parse", details: true }));
        const originalSyntax = await probe(syntaxOracle, [], requests), candidateSyntax = await probe(candidate, [...managed ? [dll] : [], "--scan-lines"], requests);
        for (let i = 0; i < batch.length; i++) {
            const f = batch[i], source = Buffer.from(f.input.text, "base64"), parsed = ts.createSourceFile(f.input.fileName, source.toString(), ts.ScriptTarget.Latest, true);
            const independent = { version: ts.version, errors: parsed.parseDiagnostics.length, sourceSha256: sha256(source) };
            const syntax = { expectedHash: originalSyntax[i].hash, actualHash: candidateSyntax[i].hash };
            const policy = classifyNameDifference(f.input, f.actual, exported[i], rebound[i], syntax, independent);
            if (policy) permitted.push({ ...f, policy, independent, syntax, syntaxFingerprint: exported[i].syntaxFingerprint, candidateTree: exported[i].tree, rebound: rebound[i].data, parserEvidence: policy.startsWith("malformed-") ? { expected: originalSyntax[i], actual: candidateSyntax[i] } : undefined });
        }
    }
}
const unexplained = failures.filter(f => !permitted.some(p => p.input.name === f.input.name));
await json(path.join(output, "checker-names-unexplained.json"), unexplained);
const summary = { timestamp: new Date().toISOString(), scope: "Lexical name and reference resolver; checker alias/merge hooks are separate", referenceRevision, managed, runtime: managed ? "managed development run" : await run(candidate, ["--native-check"]), cases: cases.length, queries, references, exact: cases.length - failures.length, strictDifferences: failures.length, permittedDifferences: permitted.length, failed: unexplained.length, inputSha256: sha256(JSON.stringify(cases)), candidateSha256: sha256(await readFile(managed ? dll : candidate)), oracleSha256: sha256(await readFile(oracle)) };
await json(path.join(output, "checker-names-summary.json"), summary);
if (option("--record")) {
    const prefix = path.join(root, `csharp/compatibility/evidence/${option("--record")}`), archive = gzipSync(JSON.stringify(permitted));
    await writeFile(prefix + "-differences.outputs.json.gz", archive);
    await json(prefix + "-differences.json", { archiveSha256: sha256(archive), cases: permitted.map(p => ({ name: p.input.name, fileName: p.input.fileName, sourceSha256: sha256(Buffer.from(p.input.text, "base64")), inputSha256: sha256(JSON.stringify(p.input)), expectedSha256: sha256(JSON.stringify(p.expected)), actualSha256: sha256(JSON.stringify(p.actual)), policy: p.policy, independent: p.independent, syntax: p.syntax, syntaxFingerprint: p.syntaxFingerprint, rationale: "The parser trees differ. Go binding and resolution of the complete candidate tree exactly reproduce candidate symbols, diagnostics, callback state and reference queries.", reproduce: `node csharp/tools/checker-names.mjs --corpus --no-build --filter ${JSON.stringify(p.input.name)} --strict` })) });
    await json(prefix + ".json", summary);
}
console.log(summary);
if (unexplained.length || process.argv.includes("--strict") && failures.length) {
    console.log(unexplained.slice(0, 8).map(f => f.input.name));
    process.exitCode = 1;
}
