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
function add(name, sources, options = {}, aliases = false, typeNodes = false, members = false) {
    for (const concurrency of [1, 4]) {
        const files = Object.fromEntries(Object.entries(sources).map(([name, text]) => [`/project/${name}`, Buffer.from(text).toString("base64")]));
        const input = { name: `${name}:${concurrency}`, files, roots: Object.keys(files), options, concurrency };
        if (aliases) input.aliases = true;
        if (typeNodes) input.typeNodes = true;
        if (members) input.members = true;
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
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                primitives: "type A = any; type U = unknown; type S = string; type N = number; type B = bigint; type Bool = boolean; type Sym = symbol; type V = void; type Undefined = undefined; type Null = null; type Never = never; type O = object;",
                literals: "type A = 'hello'; type B = 42; type C = -1; type D = -0; type E = 0x10; type F = 123n; type G = -0xffn; type H = true | false;",
                unions: "type A = string | number; type B = 1 | 2 | 1; type C = A | boolean; type D = 'a' | 'b' | string; type E = undefined | null | number;",
                intersections: "type A = string & number; type B = string & {}; type C = 'a' | 'b' | B; type D = (number & {}) | 1 | 2;",
                arrays: "type A = string[]; type B<T> = T[]; type C = B<number>; type D = readonly number[]; type E<T> = Array<T>; type F = E<string>;",
                tuples: "type A = []; type B = [string, number?]; type C = readonly [name: string, age?: number]; type D = [string, ...number[]]; type E<T extends unknown[]> = [string, ...T]; type F = E<[number, boolean]>;",
                defaults: "type A<T = string, U = T> = [T,U]; type B = A; type C = A<number>; interface Box<T=string> { value: T } type D = Box; type E = Box<number>;",
                objects: "type A = {}; type B<T> = { value: T; }; type C = B<string>; type F<T> = (value:T) => T; type G = F<number>; type H = new<T>(value:T) => B<T>;",
                templates: "type A = `x${'a'|'b'}`; type B<T extends string> = `get${T}`; type C = B<'One' | 'Two'>; type D = `${number}`;",
                intrinsic: "type Uppercase<S extends string> = intrinsic; type Lowercase<S extends string> = intrinsic; type NoInfer<T> = intrinsic; type A = Uppercase<'one'>; type B<T> = Lowercase<T>; type C = B<'TWO'>; type D<T> = NoInfer<T>;",
                recursive: "type A = A[]; type B<T> = [T, B<T>?]; type C = B<string>;",
                circular: "type A = B; type B = A; type C<T> = C<T>;",
                arity: "type A<T> = T; type B = A; type C = A<string,number>; interface I<T> {} type D = I; type E = I<string,number>; type F = string<number>;",
                unresolved: "type A = Missing; type B = Missing<string>; type C = Missing<string>; type D = Namespace.Missing<number>;",
                mapped: "type M<T> = { [P in keyof T]: T[P] }; type K<T> = keyof T; type V<T,P extends keyof T> = T[P];",
                thisType: "class A<T> { value: T; self: this; static invalid: this; method<U>(x: U): this { return this; } } interface I { self: this } type Invalid = this;",
                enums: "enum E { A, B, C = 7 } enum F { A='a', B='b' } enum Empty {} type A = E; type B = E.B; type C = F.A | F.B;",
                nestedCaptures: "function outer<T>() { type A<U=T> = readonly [U,T]; type B = A; class C<V=T> { value: A<V>; } type D = C; }",
                aliasChains: "type A<T=string> = T; type B<U=number> = A<U>; type C = B; type D<V> = B<V>; type E = D<boolean>;",
                uniqueSymbols: "declare const token: unique symbol; interface I { readonly id: unique symbol } class C { static readonly id: unique symbol; readonly value: unique symbol; }",
                predicates: "type A = (value: unknown) => value is string; type B = (value: unknown) => asserts value is number;",
                restAliases: "type ArrayAlias<T> = T[]; type T = [...(string[])]; type U = [head: number, ...tail: string[]]; type V<T> = [T, ...(number[])];",
                enumDuplicates: "enum E { A = 1, B = 1, C = 2 } enum F { A='same', B='same' } type T = E.A | E.B; type U = F.A;",
            })
        ) add(`type-nodes:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes, target: "esnext" }, false, true);
        add(`type-nodes:imported:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "a.ts": "export type A<T> = T[]; export interface Box<T> { value: T }", "b.ts": "import {A, Box} from './a'; type B = A<string>; type C = Box<number>;" }, { strict, exactOptionalPropertyTypes, module: "esnext" }, false, true);
    }
}
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                properties: "interface I { a: string; b?: number; readonly c: boolean } type A = I;",
                inherited: "interface Base<T> { value: T; optional?: T } interface Child<U> extends Base<U> { own: number } type C = Child<string>;",
                override: "interface A { a: string; z: number } interface B { b: boolean; a: number } interface C extends A,B { a: boolean; c: string }",
                thisType: "interface A<T> { self: this; value: T; clone(): this } interface B extends A<string> { other: this }",
                object: "type Box<T> = { value: T; nested: { inner: T }; method(value:T): T }; type B = Box<number>;",
                functions: "type F<T> = (value: T) => T; type G = F<string>; declare function f<T>(x: T, y?: number): T;",
                overloads: "function f(x: string): number; function f(x: number): string; function f(x: string | number): string | number { return x; }",
                parameters: "type F = (this: { id: number }, x: 'x', y?: string, ...rest: number[]) => void; function g(x: string = '', y: number): void {}",
                constructs: "type C<T> = new (value: T) => { value:T }; type D = C<string>; type A = abstract new <T>(value: T) => T;",
                predicates: "type F = (x: unknown) => x is string; type A = (x: unknown) => asserts x is number; interface I { test(): this is I; assert(): asserts this }",
                calls: "interface F<T> { (value: T): T; new (value: T): { value:T }; p: T } interface G extends F<string> { (value: number): number }",
                indexes: "interface I<T> { [s: string]: T; [n: number]: T; } type S = I<string>; interface R { readonly [s: symbol]: number; [k: `data-${string}`]: boolean }",
                indexUnions: "interface I { [s: string | symbol]: number; [s: string]: boolean } interface J extends I { [s: string]: string }",
                recursive: "interface I<T> { child: I<T>; value: T } type S = I<string>; type O = { next: O; value: number };",
                merge: "interface I<T> { a:T; (x:T):T } interface I<T> { b:T; (x:number):number } type S = I<string>;",
                arrays: "type A = string[]; type R = readonly number[]; type T = [string, number?];",
                invalidBase: "type N = number; interface A extends N {} interface B extends Missing {}",
                cycles: "interface A extends B {} interface B extends A {}",
                unicode: "interface 日本<T> { 値:T; '__name':string; '\\uFDD0hidden':number; '\\ud800':boolean; 1: T } type S=日本<string>;",
                inheritedDefaults: "interface A<T = number> { a:T } interface B<U = string> extends A<U> { b:U } interface C extends B {}",
                overloadInheritance: "interface A { (x:string):number; (x:number):string } interface B extends A { (x:boolean):boolean } type F<T> = { (x:T):T; <U>(x:U):U }; type S=F<string>;",
                assertions: "type A=(x:unknown)=>asserts x; interface I { a(): asserts this; b(): this is I }",
            })
        ) add(`members:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes, target: "esnext" }, false, true, true);
    }
}
let selected = process.argv.includes("--members") ? cases.filter(c => c.members) : process.argv.includes("--type-nodes") ? cases.filter(c => c.typeNodes && !c.members)
    : cases.filter(c => !c.typeNodes && Boolean(c.aliases) === process.argv.includes("--aliases"));
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
    scope: process.argv.includes("--members") ? "Source structured members, interface bases, signatures and index signatures with annotated value dependencies; full checker integration remains incomplete" : process.argv.includes("--type-nodes") ? "Source type-node evaluation, declared aliases and references with explicit semantic dependencies; full checker integration remains incomplete" : process.argv.includes("--aliases")
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
