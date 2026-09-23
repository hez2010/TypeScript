import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    output,
    referenceRevision,
    root,
    run,
    sha256,
} from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc"), oracle = path.join(output, "checker-program-oracle.exe");
const managed = process.argv.includes("--managed"), native = path.join(output, "phase4-native");
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const candidate = managed ? dotnet : path.join(native, "TypeScript.Compatibility.exe");
await mkdir(path.join(source, "cmd/checker-program-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/checker-program/main.go"), path.join(source, "cmd/checker-program-probe/main.go"));
await copyFile(path.join(root, "csharp/oracle/checker-program/bridge.go"), path.join(source, "internal/checker/csharp_program_probe.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/checker-program-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) {
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", native],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}

const cases = [];
const library = `interface IArguments {} interface Object {} interface Function {} interface CallableFunction extends Function {} interface NewableFunction extends Function {} interface String {} interface Number {} interface Boolean {} interface RegExp {} interface Array<T> { length: number; [n: number]: T; } interface ReadonlyArray<T> { readonly length: number; readonly [n: number]: T; } interface ThisType<T> {}`;
function add(name, sources, options = {}, aliases = false) {
    for (const concurrency of [1, 4]) {
        const files = Object.fromEntries(Object.entries(sources).map(([name, text]) => [`/project/${name}`, Buffer.from(text).toString("base64")]));
        const input = { name: `${name}:${concurrency}`, files, roots: Object.keys(files), options, concurrency };
        if (aliases) input.aliases = true;
        cases.push(input);
    }
}
for (const strict of [false, true]) {
    for (const lib of [false, true]) {
        const base = lib ? { "globals.d.ts": library } : {};
        for (
            const [name, text] of Object.entries({
                empty: "",
                classes: "class Plain {} class C<T> { value: T; self(): this { return this; } } interface I {} interface J<T> { value: T } interface K { self: this } interface L extends I {} interface M extends K {} interface N extends C<string> {}",
                nested: "function outer<T>() { class Inner<U> { value: T; other: U; self: this; } interface Local<V> { x: T; y: V; } type Alias<W> = { x: W; y: T }; }",
                signatures: "type F<T> = <U>(x: T, y: U) => T; interface I<T> { method<U>(x: T, y: U): this; <V>(x: V): T; new<W>(x: W): I<W>; }",
                conditional: "type C<T> = T extends infer U ? { a: U; b: T } : never; type M<T> = { [P in keyof T]: T[P] };",
                namespaces: "namespace N { export interface A { self: this } export interface B<T> extends A { value: T } } interface X extends N.A {} namespace N { export interface A { value: number } }",
                circular: "interface A extends B {} interface B extends A {} interface C extends C {}",
                classExpression: "function f<T>() { const C = class Inner<U> { x: T; y: U; self: this; }; return C; }",
                functions: "const fn = function inner<T>(value: T): T { return value; }; const arrow = <T>(value: T): T => value;",
                undefinedType: "interface undefined { value: number }",
                undefinedValue: "var undefined: number; let globalThis: number;",
            })
        ) add(`${name}:${strict}:${lib}`, { ...base, "main.ts": text }, { strict });
        add(`merge:${strict}:${lib}`, { ...base, "a.ts": "interface I<T> { a: T; } namespace N { export interface A { a: number } }", "b.ts": "interface I<T> { b: T; } namespace N { export interface A { b: string } }" }, { strict });
        add(`global-augment:${strict}:${lib}`, { ...base, "a.ts": "interface I { a: number; }", "b.ts": "export {}; declare global { interface I { b: string; self: this; } interface Added<T> { value: T } }" }, { strict });
        add(`module-augment:${strict}:${lib}`, { ...base, "a.ts": "export interface I<T> { a: T; }", "b.ts": "import './a'; declare module './a' { interface I<T> { b: T; } }" }, { strict });
        add(`ambient:${strict}:${lib}`, { ...base, "a.d.ts": "declare module 'pkg' { export interface I<T> { a: T; } } declare module '*.text' { export interface Text { a: number } }", "b.d.ts": "declare module 'pkg' { export interface I<T> { b: T; } } declare module '*.text' { export interface Text { b: string } }" }, { strict });
        add(`umd:${strict}:${lib}`, { ...base, "a.d.ts": "export as namespace First; export interface A {}", "b.d.ts": "export as namespace First; export interface B {}" }, { strict });
        add(`pattern-augment:${strict}:${lib}`, { ...base, "ambient.d.ts": "declare module '*.pkg' { export interface Base { value: number } }", "main.ts": "export {}; declare module 'thing.pkg' { interface Added { self: this } }" }, { strict });
        add(`missing-augment:${strict}:${lib}`, { ...base, "main.ts": "export {}; declare module 'missing' { interface Added {} }" }, { strict });
        add(`ambient-missing-augment:${strict}:${lib}`, { ...base, "main.d.ts": "export {}; declare module 'missing' { interface Added {} }" }, { strict });
        add(`unicode:${strict}:${lib}`, { ...base, "main.ts": "namespace 日本 { export interface 基本<T> { 値: T; 自分: this } } interface __ユーザー<T> extends 日本.基本<T> { 値: T }" }, { strict });
    }
}
for (const strict of [false, true]) {
    for (const definition of ["interface Array {}", "interface Array<T, U> {}", "type Array = number", "class Array<T> {}", "interface Array<T> {} interface ReadonlyArray<T,U> {}"]) add(`global-array:${strict}:${definition}`, { "main.d.ts": definition }, { strict });
}
for (const strict of [false, true]) {
    const source = "export class Base { self: this; } export interface Shape<T> { value: T } export const value = 1; export default Base;";
    for (
        const [name, declaration] of Object.entries({
            named: "import { Base, Shape, value } from './a'; interface I extends Base {} type T = Shape<string>; export { Base, Shape, value };",
            default: "import Default from './a'; import { default as Other } from './a'; interface I extends Default {} export { Other };",
            namespace: "import * as NS from './a'; interface I extends NS.Shape<string> {} export { NS };",
            typeClause: "import type { Base, Shape } from './a'; interface I extends Base {} export { Base, Shape };",
            typeSpecifier: "import { type Base, type Shape, value } from './a'; interface I extends Base {} export { Base, Shape, value };",
            typeDefault: "import type Default from './a'; interface I extends Default {} export { Default };",
            typeNamespace: "import type * as NS from './a'; interface I extends NS.Shape<string> {} export { NS };",
            reexport: "export { Base as C, Shape, value, default as D } from './a';",
            typeReexport: "export type { Base as C, Shape, default as D } from './a';",
            namespaceReexport: "export * as NS from './a'; export type * as Types from './a';",
        })
    ) {
        add(`alias:${name}:${strict}`, { "a.ts": source, "b.ts": declaration }, { strict, module: "esnext" }, true);
    }
    add(`alias:chain:${strict}`, { "a.ts": source, "b.ts": "export type {Base as C} from './a'; export {Shape as S} from './a';", "c.ts": "import { C, S } from './b'; interface I extends C {} type T = S<string>; export { C as D, S };" }, { strict, module: "esnext" }, true);
    add(`alias:internal:${strict}`, { "main.ts": "namespace N { export namespace Inner { export interface I { self: this } export const value = 1; } } import A = N; import B = A.Inner; import C = B.I; interface I2 extends C {}" }, { strict }, true);
    add(`alias:cycle:${strict}`, { "main.ts": "import A = B; import B = A;" }, { strict }, true);
    add(`alias:missing-module:${strict}`, { "main.ts": "import { Missing } from 'unavailable-package'; export { Missing };" }, { strict, module: "esnext" }, true);
    add(`alias:missing-name:${strict}`, { "main.ts": "export { nonexistent };" }, { strict, module: "esnext" }, true);
    add(`alias:export-equals:${strict}`, { "a.ts": "namespace N { export interface I { self: this } } export = N;", "b.ts": "import A = require('./a'); interface I extends A.I {}" }, { strict, module: "commonjs" }, true);
    add(`alias:umd:${strict}`, { "main.d.ts": "export as namespace U; export interface I {}" }, { strict }, true);
    add(`alias:star:${strict}`, { "a.ts": source, "b.ts": "export * from './a';", "c.ts": "import { Base, Shape, value } from './b'; interface I extends Base {} export { Base, Shape, value };" }, { strict, module: "esnext" }, true);
    add(`alias:type-star:${strict}`, { "a.ts": source, "b.ts": "export type * from './a';", "c.ts": "import { Base, Shape, value } from './b'; export { Base, Shape, value };" }, { strict, module: "esnext" }, true);
    add(`alias:star-override:${strict}`, { "a.ts": source, "b.ts": "export type * from './a'; export * from './a';", "c.ts": "import { Base, value } from './b'; export {Base, value};" }, { strict, module: "esnext" }, true);
    add(`alias:star-cycle:${strict}`, { "a.ts": "export * from './b'; export class A {}", "b.ts": "export * from './a'; export class B {}", "c.ts": "import { A, B } from './a'; export {A, B};" }, { strict, module: "esnext" }, true);
    add(`alias:star-conflict:${strict}`, { "a.ts": "export const value = 1;", "b.ts": "export const value = 2;", "c.ts": "export * from './a'; export * from './b';", "d.ts": "import { value } from './c'; export {value};" }, { strict, module: "esnext" }, true);
    add(`alias:mixed:${strict}`, { "a.ts": "export const value = 1;", "b.ts": "export {value} from './a'; export type value = number;", "c.ts": "import {value} from './b'; export {value};" }, { strict, module: "esnext" }, true);
    add(`alias:namespace-assignment:${strict}`, { "main.ts": "namespace N { export = nonexistent; }" }, { strict }, true);
    add(`alias:require:${strict}`, { "a.ts": "namespace N { export interface I {} } export = N;", "b.js": "const A = require('./a');" }, { strict, allowJs: true, module: "commonjs" }, true);
    add(`alias:internal-type-export:${strict}`, { "a.ts": source, "b.ts": "export type {Base} from './a';", "c.ts": "import * as NS from './b'; import X = NS.Base;" }, { strict, module: "esnext" }, true);
    add(`alias:internal-type-import:${strict}`, { "a.ts": "export namespace N { export class C {} }", "b.ts": "import type {N} from './a'; import X = N.C;" }, { strict, module: "esnext" }, true);
    add(`alias:star-explicit:${strict}`, { "a.ts": "export const value = 1;", "b.ts": "export const value = 2;", "c.ts": "export * from './a'; export * from './b'; export {value} from './a';", "d.ts": "import {value} from './c'; export {value};" }, { strict, module: "esnext" }, true);
    for (const length of [2, 8, 32]) {
        const chain = { "m0.ts": "export class C {}" };
        for (let i = 1; i <= length; i++) chain[`m${i}.ts`] = `export ${i % 3 === 0 ? "type " : ""}{C} from './m${i - 1}';`;
        chain["use.ts"] = `import {C} from './m${length}'; export {C};`;
        add(`alias:chain-${length}:${strict}`, chain, { strict, module: "esnext" }, true);
    }
}
let selected = cases.filter(c => Boolean(c.aliases) === process.argv.includes("--aliases"));
if (option("--filter")) selected = selected.filter(c => c.name.includes(option("--filter")));
async function probe(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true }), stdout = [], stderr = [];
        child.stdout.on("data", b => stdout.push(b));
        child.stderr.on("data", b => stderr.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            const lines = Buffer.concat(stdout).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code) reject(Error(`${command}: ${selected[lines.length]?.name}: ${Buffer.concat(stderr)}`));
            else resolve(lines.map(JSON.parse));
        });
        child.stdin.end(selected.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
await json(path.join(output, "checker-program-inputs.json"), selected);
const expected = await probe(oracle, []);
const actual = await probe(candidate, [...managed ? [dll] : [], "--checker-program-lines"]);
assert.equal(actual.length, selected.length);
const failures = [];
for (let i = 0; i < selected.length; i++) {
    try {
        assert.deepEqual(actual[i], expected[i]);
    }
    catch {
        failures.push({ input: selected[i], expected: expected[i], actual: actual[i] });
    }
}
await json(path.join(output, "checker-program-failures.json"), failures);
const summary = {
    timestamp: new Date().toISOString(),
    scope: process.argv.includes("--aliases")
        ? "Program-backed alias targets, type-only chains and module exports with explicit semantic dependencies; full checker integration remains incomplete"
        : "Program-owned global symbols, class/interface headers and generic scopes with explicit semantic dependencies; full checker integration remains incomplete",
    referenceRevision,
    managed,
    runtime: managed ? "managed development run" : await run(candidate, ["--native-check"]),
    cases: selected.length,
    exact: selected.length - failures.length,
    failed: failures.length,
    inputSha256: sha256(JSON.stringify(selected)),
    referenceOutputSha256: sha256(JSON.stringify(expected)),
    outputSha256: sha256(JSON.stringify(actual)),
    candidateSha256: sha256(await readFile(managed ? dll : candidate)),
    oracleSha256: sha256(await readFile(oracle)),
};
await json(path.join(output, "checker-program-summary.json"), summary);
if (option("--record")) await json(path.join(root, `csharp/compatibility/evidence/${option("--record")}.json`), summary);
console.log(summary);
if (failures.length) {
    console.log(failures.slice(0, 5).map(f => f.input.name));
    process.exitCode = 1;
}
