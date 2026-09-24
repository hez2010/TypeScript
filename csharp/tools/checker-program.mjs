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
function add(name, sources, options = {}, aliases = false, typeNodes = false, members = false, values = false, properties = false, signatures = false, identity = false, assignability = false) {
    for (const concurrency of [1, 4]) {
        const files = Object.fromEntries(Object.entries(sources).map(([name, text]) => [`/project/${name}`, Buffer.from(text).toString("base64")]));
        const input = { name: `${name}:${concurrency}`, files, roots: Object.keys(files), options, concurrency };
        if (aliases) input.aliases = true;
        if (typeNodes) input.typeNodes = true;
        if (members) input.members = true;
        if (values) input.values = true;
        if (properties) input.properties = true;
        if (signatures) input.signatures = true;
        if (identity) input.identity = true;
        if (assignability) input.assignability = true;
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
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                annotated: "declare const literal: 'x'; declare let count: number; declare var flag: boolean; interface I { value?: string; method?():number }",
                accessors: "interface I { get value():string; set value(v:string|number); get only():number; set write(v:boolean); }",
                genericAccessors: "interface I<T> { get value():T; set value(v:T|undefined); } type S=I<string>; type Obj<T>={get value():T; set value(v:T)}; type N=Obj<number>;",
                classProperties: "class C<T> { value:T; optional?:T; static s:string; constructor(value:T) {} method(x:T):T { return x; } }",
                classDefault: "abstract class C<T=string> { value:T; static count:number; } class D { name:string }",
                classAccessors: "class C<T> { get value():T { throw 1; } set value(v:T|undefined) {} static get count():number { return 1; } static set count(v:number|string) {} }",
                autoAccessors: "class C<T> { accessor value:T; static accessor count:number; }",
                modules: "namespace N { export const value:string; export interface I { p:number } export function f(x:number):string; } namespace N { export const other:boolean; }",
                enums: "enum N { A, B=4, C=4 } enum S { A='a', B='b' } enum Empty {}",
                optionalMethods: "interface I<T> { method?(x:T):T } type S=I<string>; type A={f?: (x:number)=>string}",
                internalAliases: "namespace N { export const value:string; export function f(x:number):number; export interface I {} } import A=N; import B=N.value; import C=N.I;",
                unannotatedAccessors: "interface I { get value(); get other(); set other(v:string); }",
                accessorThis: "interface I<T> { get value(this: I<T>): T; set value(v:T); get other():T; set other(this:I<T>,v:T); } type S=I<string>;",
                privateAccessors: "declare class C { private get value(); private set other(v:number); }",
                ambientModule: "declare module 'pkg'; import X = require('pkg');",
                valueAliasCycles: "import A=B; import B=A;",
            })
        ) add(`values:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes, target: "esnext" }, false, true, true, true);
        add(`values:imports:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "a.ts": "export const value:string; export function f(x:number):boolean; export interface I {}", "b.ts": "import {value,f,I} from './a'; import * as NS from './a'; export {value,f,I,NS};" }, { strict, exactOptionalPropertyTypes, module: "esnext" }, false, true, true, true);
    }
}
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                shared: "type U={value:string,a:number}|{value:number,b:string}; type I={value:string,a:number}&{value:number,b:string};",
                optional: "type U={optional?:string}|{optional:number}; type I={optional?:string}&{optional?:number};",
                readonly: "type U={readonly value:string}|{value:string}; type I={readonly value:string}&{value:string};",
                partial: "type U={value:string}|{other:number}; type I={value:string}&{other:number};",
                discriminants: "type I={kind:'a',value:string}&{kind:'b',value:number}; type U=I|{kind:'c',value:boolean};",
                neverProperty: "type A={value:never}&{value:string}; type B={kind?:'a'}&{kind?:'b'};",
                accessors: "interface A { get value():string; set value(v:string|number); } interface B { get value():number; set value(v:boolean); } type U=A|B; type I=A&B;",
                deferred: "type U={value:string}|{value:number}|{value:boolean}; type I={value:string}&{value:number}&{value:boolean};",
                private: "class A { private value:string; } class B { private value:number; } type U=A|B; type I=A&B;",
                protected: "class A { protected value:string; } class B { public value:number; } type U=A|B; type I=A&B;",
                generic: "interface Box<T> { value:T; length:number } type U=Box<string>|Box<number>; type I=Box<string>&Box<number>;",
                genericPrivate: "class Box<T> { private value:T; length:number } type U=Box<string>|Box<number>; type I=Box<string>&Box<number>;",
                indexes: "type U={value:string}|{[s:string]:number}; type I={readonly [s:string]:string}&{[s:string]:number};",
                unionIndexes: "type U={readonly [n:number]:string;[s:string]:unknown}|{[n:number]:number;[s:string]:unknown};",
                arrays: "type U=string[]|number[]; type I=string[]&number[]; type T=[string]|[number,boolean];",
                primitives: "type S=string; type N=number; type U=string|number; type A=unknown; type O=object; type B=boolean;",
                constrained: "interface Box<T> { value:T } type C<T extends Box<string>>=T; type I<T extends {value:string}&{other:number}>=T;",
                recursive: "interface A { value:A; kind:'a' } interface B { value:B; kind:'b' } type U=A|B;",
                numericIndexes: "type U={'0':string;'1':boolean}|{[n:number]:number}; type I={ [n:number]:string }&{ [n:number]:number };",
                tupleRest: "type U=[string, ...number[]]|[boolean, ...string[]]; type R=readonly [string]|readonly [number,boolean];",
                literalKinds: "type B=bigint; type S=symbol; type N=null|undefined; type U=unknown; type A=any; type E={};",
                mapped: "type M<T extends string[]> = { [K in keyof T]: T[K] }; type A=M<['a','b']>; type N<T extends readonly number[]> = { readonly [K in keyof T]:T[K] };",
            })
        ) add(`properties:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true, false, false, true);
        const wrappers = library.replace("interface Object {}", "interface Object { toString():string }")
            .replace("interface Function {}", "interface Function { apply(this:Function,x:unknown):unknown }")
            .replace("interface String {}", "interface String { readonly length:number; value:string }")
            .replace("interface Number {}", "interface Number { value:number }");
        add(`properties:augmentation:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": wrappers, "main.ts": "type U=string|number; type F=(x:number)=>string; type M=F|{value:string}; type I={value:string}&{}; type O={toString:string}|{};" }, { strict, exactOptionalPropertyTypes }, false, true, false, false, true);
    }
}
const numberStrings = new Set(["", " ", "-0", "+0", "NaN", "Infinity", "+Infinity", "-Infinity", "inf", "nan", ".", "+", "-", ".5", "1.", "1e", "1e+", "01", "0x", "0b", "0o", "0x1p0", "+0x1", "-0x1", "0_1", "1n", "1,000", "0b2", "0o8", "0xg", "1 1", "1e9999", "-1e-9999"]);
for (const ch of ["\t", "\n", "\r", "\v", "\f", "\u00a0", "\u1680", "\u2000", "\u2007", "\u2028", "\u2029", "\u202f", "\u205f", "\u3000", "\ufeff", "\u0085", "\u180e", "\u200b"]) {
    for (const s of [ch, `${ch}1${ch}`, `1${ch}1`]) numberStrings.add(s);
}
let numberSeed = 0x4199a421;
const numberRandom = () => numberSeed = (Math.imul(numberSeed, 1664525) + 1013904223) >>> 0;
for (let i = 0; i < 256; i++) {
    const value = (BigInt(numberRandom()) << BigInt((i * 5) % 1100)) + BigInt(numberRandom());
    for (const [prefix, radix] of [["0x", 16], ["0o", 8], ["0b", 2]]) numberStrings.add(prefix + value.toString(radix));
    for (const s of [value.toString(), `-${value}`, `${numberRandom()}.${numberRandom()}e${i - 128}`, `-${numberRandom()}e-${i + 250}`]) numberStrings.add(s);
}
const maxFiniteInteger = (1n << 1024n) - (1n << 971n), halfUlp = 1n << 970n;
for (const value of [maxFiniteInteger - halfUlp, maxFiniteInteger - halfUlp + 1n, maxFiniteInteger, maxFiniteInteger + halfUlp - 1n, maxFiniteInteger + halfUlp, maxFiniteInteger + halfUlp + 1n]) {
    for (const [prefix, radix] of [["", 10], ["0x", 16], ["0o", 8], ["0b", 2]]) numberStrings.add(prefix + value.toString(radix));
}
for (const s of ["0b" + "0".repeat(20000) + "1", "0".repeat(20000) + "1", "0x" + "f".repeat(20000) + "G"]) numberStrings.add(s);
add("properties:numeric-strings", { "globals.d.ts": library }, {}, false, true, false, false, true);
for (const input of cases.filter(c => c.name.startsWith("properties:numeric-strings:"))) input.numberStrings = [...numberStrings].map(s => Buffer.from(s).toString("base64")).concat(["eda080", "edb080", "31eda080"].map(s => Buffer.from(s, "hex").toString("base64")));
for (const [name, options] of [["default", {}], ["disabled", { strict: false }], ["overrides", { strict: false, strictNullChecks: true, strictBindCallApply: true, noImplicitAny: true, strictBuiltinIteratorReturn: true }]]) add(`properties:strict-${name}`, { "globals.d.ts": library, "main.ts": "interface I { optional?:string; get implicit(); } type T=undefined|string; type BuiltinIteratorReturn=intrinsic; type R=BuiltinIteratorReturn;" }, options, false, true, false, false, true);
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                matching: "type A=(x:string)=>number; type B=(x:string)=>boolean; type U=A|B; type I=A&B;",
                incompatible: "type A=(left:string)=>number; type B=(right:number)=>boolean; type U=A|B; type I=A&B;",
                excess: "type A=(x:string)=>number; type B=(x:string,y?:number)=>boolean; type U=A|B;",
                arity: "type A=(x:string,y:number)=>string; type B=(x:number)=>number; type C=()=>void; type U=A|B|C;",
                rest: "type A=(x:string,...args:number[])=>string; type B=(a:number,b:boolean)=>number; type U=A|B;",
                tupleRest: "type A=(...args:[head:string,tail?:number])=>string; type B=(x:string,y?:number)=>number; type U=A|B;",
                variadic: "type A=(...args:[string,...number[]])=>string; type B=(x:number,y?:boolean)=>number; type U=A|B;",
                trailingRest: "type A=(...args:[string,...number[],boolean])=>string; type B=(...args:[number,...string[]])=>number; type U=A|B;",
                labels: "type A=(...[first,second,...tail]:[string,number,...boolean[]])=>void; type B=(...other:[string,number,boolean?])=>void; type U=A|B;",
                voidArity: "type A=(x:string,y:void)=>string; type B=(x:number,y:number|void,z?:boolean)=>number; type U=A|B; function f(x:string='',y:void):void {}",
                thisTypes: "type A=(this:string,x:number)=>string; type B=(this:number,x:number)=>number; type U=A|B;",
                absentThis: "type A=(this:string,x:number)=>string; type B=(x:number)=>number; type U=A|B; type I=A&B;",
                generics: "type A=<T>(x:T)=>T; type B=<U>(value:U)=>U; type U=A|B; type I=A&B;",
                constraints: "type A=<T extends string>(x:T)=>T; type B=<U extends number>(x:U)=>U; type U=A|B;",
                defaults: "type A=<T=string>(x:T)=>T; type B=<U=number>(x:U)=>U; type U=A|B;",
                dependent: "type A=<T,U extends T>(x:U)=>T; type B=<X,Y extends X>(x:Y)=>X; type U=A|B;",
                genericMixed: "type A=<T>(x:T)=>T; type B=(x:string)=>number; type U=A|B;",
                overloads: "interface A { (x:string):number; (x:number):string } type B=(x:boolean)=>boolean; type U=A|B;",
                multiOverloads: "interface A { (x:string):number; (x:number):string } interface B { (x:string):boolean; (x:boolean):number } type U=A|B;",
                inherited: "interface A { (x:string):string } interface B extends A {} interface C extends A {} type D=(x:number)=>number; type U=B|C|D;",
                predicates: "type A=(x:unknown)=>x is string; type B=(x:unknown)=>x is number; type U=A|B; type I=A&B; type F=(x:unknown)=>false; type V=A|F;",
                assertions: "type A=(x:unknown)=>asserts x is string; type B=(x:unknown)=>asserts x is number; type U=A|B;",
                constructors: "type A=new(x:string)=>string; type B=new(x:number)=>number; type U=A|B; type I=A&B;",
                mixins: "type A=new(...args:any[])=>string; type B=new(x:number)=>number; type I=A&B;",
                functionTop: "type F=(x:number)=>string; type U=Function|F;",
            })
        ) add(`signatures:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true, true, false, false, true);
        const arrays = library.replace("length: number; [n: number]: T;", "length: number; [n: number]: T; choose<U extends T>(x: U): U;")
            .replace("readonly length: number; readonly [n: number]: T;", "readonly length: number; readonly [n: number]: T; choose<U extends T>(x: U): U;");
        add(`signatures:array-member:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": arrays, "main.ts": "type A=Array<string>['choose']; type B=Array<number>['choose']; type U=A|B; type R=ReadonlyArray<number>['choose']; type M=A|R;" }, { strict, exactOptionalPropertyTypes }, false, true, true, false, false, true);
        add(`signatures:array-return:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": arrays.replaceAll("choose<U extends T>(x: U): U;", "choose<U extends T>(x: U): U[];"), "main.ts": "type A=Array<string>['choose']; type B=Array<number>['choose']; type U=A|B;" }, { strict, exactOptionalPropertyTypes }, false, true, true, false, false, true);
    }
}
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                primitives: "type R0=string;type R1=1;type R2=unknown;type R3=any;type R4=undefined;type R5=never;type R6=object;type R7='x';type R8=boolean;type R9=number;type R10=null;type R11=void;",
                properties: "type R0={value:string};type R1={value:number};type R2={value:string};type R3={value:string;extra:boolean};type R4={};",
                optional: "type R0={value?:string};type R1={value:string|undefined};type R2={value?:string};type R3={readonly value?:string};",
                nested: "type R0={a:{b:string}};type R1={a:{b:string}};type R2={a:{b:number}};",
                recursive: "interface R0{next:R0;value:string}interface R1{next:R1;value:string}interface R2{next:R2;value:number}",
                functions: "type R0=(x:string)=>number;type R1=(a:string)=>number;type R2=(x:number)=>number;type R3=(x:string)=>boolean;",
                recursiveFunctions: "type R0=(x:R0)=>R0;type R1=(x:R1)=>R1;type R2=(x:R2)=>number;",
                methods: "type R0={f(x:{a:string}):{b:number}};type R1={f(x:{a:string}):{b:number}};type R2={f(x:{a:number}):{b:number}};",
                unions: "type R0={a:string}|{b:number};type R1={b:number}|{a:string};type R2={a:string}|{b:string};",
                intersections: "type R0={a:string}&{b:number};type R1={b:number}&{a:string};type R2={a:string;b:number};",
                indexes: "type R0={[s:string]:number};type R1={[s:string]:number};type R2={readonly[s:string]:number};type R3={[n:number]:number};",
                tuples: "type R0=[string,number?];type R1=[first:string,second?:number];type R2=readonly[string,number?];type R3=[string,number];",
                classes: "class R0{value:string}class R1{value:string}class R2{private value:string}class R3{private value:string}",
                accessors: "interface R0{get value():string;set value(x:number)}interface R1{value:string}interface R2{readonly value:string}interface R3{get value():string}",
                templates: "type R0=`a${string}`;type R1=`a${number}`;type R2=`b${string}`;type R3=`a${string}`;",
                predicates: "type R0=(x:unknown)=>x is string;type R1=(x:unknown)=>x is number;type R2=(y:unknown)=>y is string;type R3=(x:unknown)=>boolean;",
                baseNormalization: "interface Base<T>{value:T}interface Empty<T> extends Base<T>{}type R0=Empty<string>;type R1=Base<string>;type R2={value:string};type R3={value:number};",
                baseThis: "interface Base<T>{value:T;self:this}interface Empty extends Base<string>{}type R0=Empty;type R1=Base<string>;type K0=Empty;",
                genericKeys: "interface Box<T>{value:T}type K0<T>=Box<T>;type K1<U>=Box<U>;type K2<T,U>=Box<[T,U]>;type K3<A,B>=Box<[B,A]>;type K4<T extends string>=Box<T>;type K5<U extends string>=Box<U>;type K6<T>=Box<Box<Box<Box<Box<T>>>>>;type K7<U>=Box<Box<Box<Box<Box<U>>>>>;",
            })
        ) add(`identity:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true, false, false, false, false, true);
    }
}
for (const depth of [3, 99, 100, 101]) {
    const parts = [`type A${depth}={value:string};type B${depth}={value:number};`];
    for (let i = depth - 1; i >= 0; i--) parts.push(`type A${i}={next:A${i + 1}};type B${i}={next:B${i + 1}};`);
    parts.push("type R0=A0;type R1=B0;");
    add(`identity:depth-${depth}`, { "globals.d.ts": library, "main.ts": parts.join("\n") }, { strict: true, exactOptionalPropertyTypes: true }, false, true, false, false, false, false, true);
}
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                primitives: "type R0=string;type R1=number;type R2='x';type R3=1;type R4=any;type R5=unknown;type R6=never;type R7=object;type R8=undefined;type R9=null;type R10=boolean;type R11=void;",
                fields: "type R0={a:string};type R1={a:string;b:number};type R2={a:number};type R3={};",
                optional: "type R0={a?:string};type R1={a:string};type R2={a:string|undefined};type R3={a?:number};type R4={};",
                readonly: "type R0={a:string};type R1={readonly a:string};type R2={readonly a:'x'};type R3={a:'x'};",
                weak: "type R0={a?:string};type R1={b?:string};type R2={a:string;b:number};type R3={};type R4=()=>string;",
                union: "type R0={a:string}|{b:number};type R1={a:'x'};type R2={a:string;b:number};type R3={a?:string};",
                intersection: "type R0={a:string}&{b:number};type R1={a:string;b:number};type R2={a:string};type R3={a?:string}&{b?:number};",
                discriminants: "type R0={kind:'a'|'b';value:number};type R1={kind:'a';value:number}|{kind:'b';value:number};type R2={kind:'a';value:string}|{kind:'b';value:number};",
                indexes: "type R0={[s:string]:number};type R1={a:number;b:number};interface R2{a:number;b:number}type R3={a?:number};type R4={[s:string]:any};",
                numberIndex: "type R0={[n:number]:string};type R1={'0':string;label:number};type R2={'0':number};type R3={[s:string]:string};",
                functions: "type R0=(x:string)=>number;type R1=(x:'x')=>number;type R2=(x:string)=>void;type R3=(x:string,y:number)=>number;type R4=()=>number;",
                methods: "type R0={f(x:string):void};type R1={f(x:'x'):void};type R2={f:(x:string)=>void};type R3={f:(x:'x')=>void};",
                callbacks: "type R0=(cb:(x:string)=>void)=>void;type R1=(cb:(x:'x')=>void)=>void;type R2=(cb:()=>string)=>void;type R3=(cb:()=>'x')=>void;",
                rest: "type R0=(...a:string[])=>void;type R1=(x:string,y?:string)=>void;type R2=(...a:[string,string])=>void;type R3=(...a:any[])=>any;",
                predicates: "type R0=(x:unknown)=>x is string;type R1=(x:unknown)=>x is 'x';type R2=(x:unknown)=>boolean;type R3=(x:unknown)=>asserts x is string;",
                constructors: "type R0=new(x:string)=>{a:string};type R1=abstract new(x:string)=>{a:string};type R2=new()=>{a:'x'};",
                tuples: "type R0=[string,number?];type R1=[string,number];type R2=readonly[string,number];type R3=[string,...number[]];type R4=string[];",
                arrays: "type R0=string[];type R1='x'[];type R2=readonly string[];type R3=readonly 'x'[];type R4=number[];",
                covariant: "interface Box<T>{value:T}type R0=Box<string>;type R1=Box<'x'>;type R2=Box<number>;",
                contravariant: "interface Sink<T>{accept:(value:T)=>void}type R0=Sink<string>;type R1=Sink<'x'>;type R2=Sink<number>;",
                invariant: "interface Both<T>{apply:(value:T)=>T}type R0=Both<string>;type R1=Both<'x'>;type R2=Both<number>;",
                independent: "interface Phantom<T>{}type R0=Phantom<string>;type R1=Phantom<number>;",
                recursive: "interface Tree<T>{value:T;next:Tree<T>}type R0=Tree<string>;type R1=Tree<'x'>;type R2=Tree<number>;",
                annotated: "interface Out<out T>{value:T}interface In<in T>{accept:(x:T)=>void}type R0=Out<string>;type R1=Out<'x'>;type R2=In<string>;type R3=In<'x'>;",
                aliasVariance: "type Box<T>={value:T};type Sink<T>={accept:(x:T)=>void};type R0=Box<string>;type R1=Box<'x'>;type R2=Sink<string>;type R3=Sink<'x'>;",
                mutualVariance: "interface A<T>{next:B<T>}interface B<T>{next:A<T>;value:T}type R0=A<string>;type R1=A<'x'>;type R2=B<string>;type R3=B<'x'>;",
                nestedVariance: "interface A<T>{next:B<A<T>>;value:T}interface B<T>{value:T}type R0=A<string>;type R1=A<'x'>;type R2=B<string>;type R3=B<'x'>;",
                methodVariance: "interface Methods<T>{accept(x:T):void}type R0=Methods<string>;type R1=Methods<'x'>;type R2=Methods<number>;",
                genericMethods: "interface Box<T>{convert<U>(value:T,other:U):U}type R0=Box<string>;type R1=Box<'x'>;type R2=Box<number>;",
                callbacksWithMembers: "type A={():string;tag:number};type B={():'x';tag:number};type R0=(cb:A)=>void;type R1=(cb:B)=>void;type R2=(cb:()=>string)=>void;",
                visibility: "class R0{private value:string}class R1{private value:string}class R2{protected value:string}class R3{value:string}",
                nullableCallback: "type R0=(cb:(()=>string)|null)=>void;type R1=(cb:(()=>'x')|undefined)=>void;type R2=(cb:()=>string)=>void;",
                optionalDiscriminants: "type R0={kind?:'a'|'b';value:number};type R1={kind?:'a';value:number}|{kind?:'b';value:number};type R2={kind:'a';value:number}|{kind:'b';value:number};",
                multiDiscriminants: "type R0={a:'a'|'b';b:1|2;value:number};type R1={a:'a';b:1;value:number}|{a:'a';b:2;value:number}|{a:'b';b:1;value:number}|{a:'b';b:2;value:number};type R2={a:'a';b:1;value:number}|{a:'b';b:2;value:number};",
                templates: "type R0=`id-${string}`;type R1=`id-${number}`;type R2='id-12';type R3='id-x';type R4=`other-${string}`;type R5={};",
                templatePlaceholders: "type R0=`${number}`;type R1=`${bigint}`;type R2='01';type R3='0xff';type R4='1_0';type R5='-1';type R6='Infinity';",
                templateSegments: "type R0=`a${string}b${number}`;type R1=`a${number}b${number}`;type R2='a12b34';type R3='ab0';type R4='a1b2b3';",
                templateUnicode: "type R0=`😀${string}`;type R1=`\\ud83d${string}`;type R2='😀x';type R3='\\ud83dx';type R4=`${number}${string}`;",
                stringMappings: "type Uppercase<S extends string>=intrinsic;type Lowercase<S extends string>=intrinsic;type R0=Uppercase<string>;type R1=Lowercase<string>;type R2='ONE';type R3='one';type R4=Uppercase<`a${string}`>;",
                templateAdjacent: "type R0=`${string}${number}`;type R1=`${number}${string}`;type R2='😀1';type R3='\\ud83d1';type R4='12';type R5='x';",
            })
        ) add(`assignability:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true, false, false, false, false, true, true);
        const variants = Array.from({ length: 12 }, (_, i) => `{kind:${i};value:string}`).join("|");
        add(`assignability:key-map:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": `type R0=${variants};type R1={kind:3;value:'x'};type R2={kind:20;value:string};` }, { strict, exactOptionalPropertyTypes }, false, true, false, false, false, false, true, true);
    }
}
for (const count of [5, 6]) {
    const first = Array.from({ length: count }, (_, i) => `'k${i}'`), second = Array.from({ length: 5 }, (_, i) => i);
    const variants = first.flatMap(a => second.map(b => `{a:${a};b:${b};value:number}`)).join("|");
    add(`assignability:discriminant-limit-${count * 5}`, { "globals.d.ts": library, "main.ts": `type R0={a:${first.join("|")};b:${second.join("|")};value:number};type R1=${variants};` }, { strict: true }, false, true, false, false, false, false, true, true);
}
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                special: "type A=keyof any;type B=keyof unknown;type C=keyof never;type D=keyof {};type E=keyof null;type F=keyof undefined;",
                object: "interface I {a:string;b?:number;readonly c:boolean;0:string;'01':number;__x:boolean}type A=keyof I;type B=keyof {x:1;y:2};",
                composite: "type A=keyof ({a:1;b:2}|{a:3;c:4});type B=keyof ({a:1}&{b:2});type C=keyof ({a:1}&{a:2});",
                signatures: "type A=keyof {[s:string]:number};type B=keyof {[n:number]:string};type C=keyof {[s:symbol]:number};type D=keyof {[s:`data-${string}`]:number};",
                tuples: "type A=keyof [string,number?];type B=keyof readonly [string,...number[]];type C=keyof string[];type D<T extends unknown[]>=keyof [1,...T];",
                generic: "type A<T>=keyof T;type B<T>=keyof (T&{});type C<T>=keyof ({a:T}&{a:'x'});type D<T>=keyof ({a:T}&{a:'x'}|{b:1});",
                mapped: "type M<T>={[P in keyof T]:T[P]};type A=keyof M<{a:1;b?:2}>;type N={[P in 'a'|'b' as `get-${P}`]:number};type B=keyof N;type C=keyof {[P in string]:number};",
                noinfer: "type NoInfer<T>=intrinsic;type A<T>=keyof NoInfer<T>;type B=A<{a:1}>;",
                accessibility: "class C{private p:string;protected q:number;public r:boolean;readonly x:string}type A=keyof C;",
                access: "interface I{a:string;b?:number;readonly c:boolean}type A=I['a'];type B=I['b'];type C=I['a'|'b'];type D=I[keyof I];type E=I['missing'];",
                indexAccess: "type I={[s:string]:number};type A=I[string];type B=I[number];type C=I[symbol];type D=I[never];type E=I[any];type F=I[boolean];type G=I[undefined];type H=I[1n];",
                tupleAccess: "type T=[string,number?];type A=T[0];type B=T['1'];type C=T[2];type D=T[-1];type E=T[number];type F=T['0'|'1'];type G=readonly [1,...string[]];type H=G[4];type I=([1]|[2,3])[1];",
                genericAccess: "type A<T,K extends keyof T>=T[K];type B=A<{a:1;b:2},'a'|'b'>;type C<T extends unknown[]>=T[number];type D<T extends unknown[]>=[1,...T,2][0];type E<T>=(T&{a:1})['a'];",
                mappedAccess: "type M<T>={[P in keyof T]?:T[P]};type A=M<{a:1;b:2}>['a'];type B<T,K extends keyof T>=M<T>[K];type C=B<{a:1},'a'>;type N<K extends string>={[P in K]:P};type D=N<'a'|'b'>['a'];",
                accessors: "interface I{get value():string;set value(v:number);get other():boolean}type A=I['value'];type B=I['value'|'other'];",
                indexNormalization: "type A<K extends number>={[s:string]:1}[K];type B<T extends number>={[s:string]:T}[number];type C<T>={[n:number]:T}['1'];",
                distribution: "type A<T>=({a:T}|{a:number})['a'];type B<T>=({a:T}&{a:number})['a'];type C<T,K extends 'a'|'b'>=({a:T;b:string}|{a:number;b:T})[K];",
                genericMapping: "type A<K extends string>={[P in K]?:P}[K];type B<K extends string>={[P in K as P&'a']:P}[K];type C<T>={[P in 'a'|'b']?:T}[keyof T];type D<T>={[P in 'a'|'b']-?:T}[keyof T];",
                nestedAccess: "type A<T,K extends keyof T>=T[K][keyof T[K]];type B<K extends string,U extends string>={[P in K]:{[Q in U]:number}}[K][U];",
                missingUnion: "type I={a:1};type A=I['x'|'y'|'a'];type B=I[1|2];type C=[1][-2|-1|2|3];",
            })
        ) add(`indexing:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes, target: "esnext" }, false, true);
    }
}
for (const input of cases.filter(c => c.name.startsWith("indexing:"))) {
    input.indexing = true;
    cases.push({ ...input, name: `${input.name}:unchecked`, options: { ...input.options, noUncheckedIndexedAccess: true } });
}
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                indexed: "function f<T extends {a:string;b:number}>(){type R0=T['a'];type R1=string;type R2=T['b'];type R3=number;type R4=T['a'|'b'];type R5=string|number;}",
                keys: "function f<T,U extends T>(){type R0=keyof T;type R1=keyof U;type R2=string;type R3=number;type R4=string|number|symbol;}",
                keyConstraints: "function f<T extends {a:string;b?:number}>(){type R0=keyof T;type R1='a';type R2='b';type R3='c';type R4='a'|'b';}",
                tupleKeys: "function f<T extends unknown[]>(){type R0=keyof [string,...T];type R1='0';type R2=number;type R3='length';type R4='1';}",
                copies: "function f<T>(){type R0=T;type R1={[P in keyof T]:T[P]};type R2={readonly[P in keyof T]:T[P]};type R3={[P in keyof T]?:T[P]};type R4={[P in keyof T]-?:T[P]};}",
                maps: "function f<K extends string>(){type R0={[P in K]:number};type R1={[Q in K]:number};type R2={[P in K]:string};type R3={[P in K]?:number};type R4={[P in K]-?:number};type R5={};}",
                boundedMaps: "function f<K extends 'a'|'b'>(){type R0={[P in K]:number};type R1={a:number;b:number};type R2={a:string;b:number};type R3={[P in K]?:number};type R4={};}",
                remapping: "function f<K extends string>(){type R0={[P in K as `x-${P}`]:number};type R1={[Q in K as `x-${Q}`]:number};type R2={[P in K as `y-${P}`]:number};type R3=keyof R0;type R4=keyof R1;type R5=keyof R2;}",
                filtering: "function f<K extends string>(){type R0={[P in K as P&'a']:number};type R1={[Q in K as Q&'a']:number};type R2={[P in K as P&'b']:number};}",
                indexedTargets: "function f<T extends {a:string;b:number},K extends keyof T>(){type R0=T[K];type R1=string;type R2=number;type R3=never;type R4=T['a'];}",
                remappedKeyof: "type M<T>={[P in keyof T as `get-${P&string}`]:T[P]};function f<T extends {a:1;b:2}>(){type R0=keyof M<T>;type R1='get-a';type R2='get-b';type R3='get-c';type R4=keyof M<{a:1;b:2}>;}",
                recursiveMaps: "function f<K extends string>(){type R0={[P in K]:R0};type R1={[P in K]:R1};type R2={[P in K]:number};type R3={[P in K]?:R3};}",
                indexedSubtypes: "function f<T extends {a:string},U extends T,K extends keyof U>(){type R0=T['a'];type R1=U['a'];type R2=U[K];type R3=T;type R4=U;}",
                optionalCopies: "type M<T>={[P in keyof T]?:T[P]};type N<T>={[P in keyof T]-?:T[P]};function f<T extends {a?:number;b:string}>(){type R0=T;type R1=M<T>;type R2=N<T>;type R3={};}",
                aliasVariance: "type M<K extends string,V>={[P in K]:V};type R0={value:M<'a',string>};type R1={value:M<string,string>};type R2={value:M<'a','x'>};type R3={value:M<'a',number>};",
                removalVariance: "type M<T>={[P in keyof T]-?:T[P]};type R0={value:M<{a?:string}>};type R1={value:M<{a:string}>};type R2={value:M<{a?:'x'}>};type R3={value:M<{a?:number}>};",
            })
        ) add(`generic-relations:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true, false, false, false, false, true, true);
    }
}
for (const input of cases.filter(c => c.name.startsWith("generic-relations:"))) input.genericRelations = true;
for (const isolatedModules of [false, true]) {
    for (
        const [name, source] of Object.entries({
            numeric: "enum E{A=1<<3,B=A|2,C=(B+1)*2,D=~A,E=2**3,F=9%4,G=-0,H=0/0,I=1/0}type T=E;",
            strings: "enum E{A='a',B=A+'b',C=`x${1+2}`,D='x'+4,E=5+'x'}type T=E;",
            references: "enum E{A=1,B=E.A+1,C=E['B']+1}enum F{A=E.A,B=E.B}type T=E|F;",
            forward: "enum E{A=A,B=C,C=3,D=F.A}enum F{A=1}type T=E|F;",
            invalidNames: "enum E{'0'=1,'NaN'=2,'Infinity'=3,[1+1]=4}type T=E;",
            ambient: "declare enum E{A,B=2,C}declare const enum F{A,B=2,C}type T=E|F;",
            constants: "const N=2;const S='a';const X=N+1;enum E{A=N,B=X,C=S,D=C+'b'}type T=E;",
            constantErrors: "const enum E{A=0/0,B=1/0,C=-1/0,D=true}type T=E;",
            missing: "enum E{A='a',B,C=3,D}type T=E;",
            numericBoundaries: "enum E{A=0%0,B=(1/0)%2,C=2%(1/0),D=(-0)%2,E=(-1)**0.5,F=1**(0/0),G=3**34,H=9**19,I=9223372036854775808**3,J=(-2)**63,K=0**0,L=3**600,M=(-1)**0.25,N=(0/0)**1,O=(0/0)**2}type T=E;",
        })
    ) add(`constants:${name}:${isolatedModules}`, { "globals.d.ts": library, "main.ts": source }, { isolatedModules, target: "esnext" }, false, true);
}
for (const strict of [false, true]) {
    for (const target of ["es2019", "esnext"]) {
        for (
            const [name, source] of Object.entries({
                literals: "const a=1;const b='a';const c=true;const d=false;const e=null;const f=0x10;const g=123n;const h=0b10n;",
                unary: "const a=-1;const b=+2;const c=~1;const d=-3n;const e=+3n;const f=!'';const g=!1;const h=!2;const i=!'x';",
                wrappers: "const a=((1));const b=typeof 'a';const c=void missing;const d=null!;const e=(true ? 1 : 2);",
                templates: "const a=`x${1}y${'z'}`;const b=`${-1}`;const c=`${true}`;const d=`${1n}`;",
                invalidUnary: "const a=++1;const b=1++;const c=~null;const d=-null;const e=!null;const f=!'a';",
            })
        ) add(`expressions:${name}:${strict}:${target}`, { "globals.d.ts": library, "main.ts": source }, { strict, target }, false, true);
    }
}
for (const isolatedModules of [false, true]) {
    add(
        `constants:cross-file:${isolatedModules}`,
        {
            "globals.d.ts": library,
            "a.ts": "export const N=3;export const S='x';export enum E{A=4}",
            "b.ts": "import {N,S,E} from './a';export enum F{A=N,B,C=S,D=E.A,E}",
        },
        { isolatedModules, target: "esnext", module: "esnext" },
        false,
        true,
    );
    add(
        `constants:special-numbers:${isolatedModules}`,
        {
            "globals.d.ts": `${library}declare var Infinity:number;declare var NaN:number;`,
            "main.ts": "const enum E{A=Infinity,B=NaN,C=-Infinity}enum F{A=1/0,B}function f(){const Infinity=3;enum G{A=Infinity}}",
        },
        { isolatedModules, target: "esnext" },
        false,
        true,
    );
}
for (const input of cases) {
    if (input.name.startsWith("constants:")) input.constants = true;
    if (input.name.startsWith("expressions:")) input.expressions = true;
}
for (const strict of [false, true]) {
    for (const target of ["es2015", "esnext"]) {
        for (
            const [name, source] of Object.entries({
                arithmetic: "const a=1+2;const b='a'+1;const c=1+'b';const d=2*3;const e=5/2;const f=1-2;const g=1%2;const h=2**3;",
                bigint: "const a=1n+2n;const b=2n*3n;const c=2n**3n;const d=2n>>>1n;const e=1n+1;const f=1n*1;const g='x'+1n;",
                invalid: "const a=true+false;const b='a'*2;const c=true|false;const d=true&false;const e=true^false;const f=null+1;const g=null*1;",
                comparisons: "const a=1<2;const b='a'<'b';const c=1<'a';const d=1===2;const e=1=='1';const f=null==undefined;const g=1n<2;",
                logical: "const a=0&&'x';const b=1&&'x';const c=''||1;const d='x'||1;const e=null??1;const f=1??2;const g=undefined??3;",
                comma: "const a=(1,2);const b=((1+2),3);const c=(void missing,4);",
                shifts: "const a=1<<32;const b=1>>>33;const c=-8>>34;const d=1<<-32;const e=1<<2;",
            })
        ) add(`binary:${name}:${strict}:${target}`, { "globals.d.ts": library, "main.ts": source }, { strict, target }, false, true);
    }
}
for (const input of cases.filter(c => c.name.startsWith("binary:"))) input.expressions = true;
const promiseLibrary = "interface Promise<T>{then(onfulfilled:(value:T)=>unknown):unknown}type Awaited<T>=T extends null|undefined?T:T extends object&{then(onfulfilled:infer F,...args:infer _):any}?F extends ((value:infer V,...args:infer _)=>any)?Awaited<V>:never:T;";
for (const strict of [false, true]) {
    for (
        const [name, source] of Object.entries({
            promises: "type A0=Promise<string>;type A1=Promise<Promise<number>>;type A2=Promise<1>|Promise<2>;type A3=null|Promise<number>;type A4=any;type A5=unknown;",
            thenables: "type A0={then(cb:(value:string)=>void):void};type A1={then(cb:(value:number)=>void):void};type A2={then():void};type A3={then(cb:number):void};type A4={then:any};type A5={then?:((cb:(value:1)=>void)=>void)};",
            overloads: "interface A0{then(cb:(value:string)=>void):void;then(cb:(value:number)=>void):void}interface A1{then(this:string,cb:(value:number)=>void):void}interface A2{then(this:void,cb:(value:number)=>void):void}",
            recursive: "interface A0{then(cb:(value:A0)=>void):void}interface A1{then(cb:(value:A2)=>void):void}interface A2{then(cb:(value:A1)=>void):void}",
            generic: "function f<T,U extends string,V extends Promise<number>>(){type A0=T;type A1=Promise<T>;type A2=U;type A3=V;type A4=Awaited<T>;}",
            primitive: "type A0=string;type A1=number;type A2=never;type A3=undefined;type A4=boolean;type A5=1|2;",
        })
    ) add(`awaited:${name}:${strict}`, { "globals.d.ts": library + promiseLibrary, "main.ts": source }, { strict }, false, true);
}
for (const input of cases.filter(c => c.name.startsWith("awaited:"))) input.awaited = true;
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                literals: "const a=1;let b=1;var c='x';const d='x';let e=true;const f=true;const g=-1;let h=~1;const i=1n;let j=1n;",
                automatic: "let a;var b;let c=null;const d=null;let e=undefined;const f=undefined;declare let g;export let h;",
                properties: "class C{readonly a='x';b='x';c;d?:number;readonly e=1;static f=true}interface I{a?;b:number;readonly c:unique symbol}",
                parameters: "declare function f(x,y=1,z?:number,...rest):void;type F=(x,y?:number,...rest)=>void;",
                catchVariables: "try{}catch(e){}try{}catch(e:unknown){}try{}catch(e:any){}try{}catch(e:string){}",
                initializers: "const a=typeof 1;let b=typeof 1;const c=void missing;let d=void missing;const e=true?1:2;let f=true?1:2;const g=`x${1}`;let h=`x${1}`;",
            })
        ) add(`initializers:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes, target: "esnext" }, false, true, true, true);
    }
}
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                direct: "type A<T>=T extends infer U?U:never;type B=A<string>;type C=A<'a'|1>;type D=A<any>;type E=A<never>;",
                arrays: "type A<T>=T extends (infer U)[]?U:never;type B=A<string[]>;type C=A<[1,2]>;type D=A<never[]>;type E<T>=T extends readonly (infer U)[]?U:never;type F=E<readonly [1,2]>;",
                properties: "type A<T>=T extends {a:infer U;b:infer U}?U:never;type B=A<{a:1;b:2}>;type C=A<{a:string;b:number}>;type D=A<{a:1}>;",
                functions: "type A<T>=T extends (x:infer U)=>void?U:never;type B=A<(x:string)=>void>;type C<T>=T extends (...args:any[])=>infer U?U:never;type D=C<()=>number>;",
                contravariance: "type A<T>=T extends {a:(x:infer U)=>void;b:(x:infer U)=>void}?U:never;type B=A<{a:(x:string)=>void;b:(x:number)=>void}>;type C=A<{a:(x:'a')=>void;b:(x:string)=>void}>;",
                tuples: "type A<T>=T extends [infer U,...infer R]?[U,R]:never;type B=A<[1,2,3]>;type C=A<[]>;type D<T>=T extends [...infer R,infer U]?[R,U]:never;type E=D<[1,2,3]>;",
                constraints: "type A<T>=T extends infer U extends string?U:never;type B=A<'a'|1>;type C<T>=T extends {x:infer U extends number}?U:never;type D=C<{x:42}>;",
                templates: "type A<T>=T extends `${infer L}-${infer R}`?[L,R]:never;type B=A<'a-b-c'>;type C<T>=T extends `${infer N extends number}`?N:never;type D=C<'42'>;type E=C<'01'>;",
                unions: "type A<T>=T extends (infer U)[]|undefined?U:never;type B=A<string[]|undefined>;type C<T>=T extends {a:infer U}|{b:infer U}?U:never;type D=C<{a:1}|{b:2}>;",
                references: "interface Box<T>{value:T}type A<T>=T extends Box<infer U>?U:never;type B=A<Box<string>>;type C=A<{value:1}>;",
                inferredConstraints: "interface Box<T extends string>{value:T}type A<T>=T extends Box<infer U>?U:never;type B=A<Box<'a'>>;type C=A<{value:number}>;",
                nested: "type A<T>=T extends {x:infer U}?U extends {y:infer V}?V:U:never;type B=A<{x:{y:string}}>;type C=A<{x:number}>;",
                mapped: "type Box<T>={[P in keyof T]:{value:T[P]}};type A<T>=T extends Box<infer U>?U:never;type B=A<{a:{value:1};b:{value:string}}>;type C=B['a'];type D=keyof B;",
                mappedTuples: "type Box<T>={[P in keyof T]:{value:T[P]}};type A<T>=T extends Box<infer U>?U:never;type B=A<[{value:1},{value:2}]>;type C=A<readonly [{value:'a'}]>;type D=A<{value:string}[]>;",
                partialMapping: "type Box<T>={readonly[P in keyof T]?:{value:T[P]}};type A<T>=T extends Box<infer U>?U:never;type B=A<{readonly a?:{value:1};b:{value:2}}>;type C=B['a'];type D=B['b'];",
                templateScalars: "type A<T>=T extends `${infer U extends bigint}`?U:never;type B=A<'123'>;type C=A<'-0'>;type D<T>=T extends `${infer U extends boolean}`?U:never;type E=D<'true'>;type F=D<'no'>;type G<T>=T extends `${infer U extends number|bigint}`?U:never;type H=G<'12'>;",
                dependentConstraints: "type Pair<T,U extends T>=[T,U];type A<X>=X extends Pair<infer T,infer U>?[T,U]:never;type B=A<[string,'a']>;type C=A<[string,number]>;type D<X>=X extends Pair<infer U,infer U>?U:never;type E=D<[string,'a']>;",
            })
        ) add(`inference:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true);
        for (
            const [name, source] of Object.entries({
                contextual: "type R0=<T>(x:T)=>T;type R1=(x:string)=>string;type R2=(x:number)=>number;type R3=(x:string)=>number;type R4=<T extends string>(x:T)=>T;",
                contextualRest: "type R0=<T>(...args:T[])=>T;type R1=(x:string,y:string)=>string;type R2=(x:string,y:number)=>string|number;type R3=<T extends unknown[]>(...args:T)=>T;type R4=(x:string,y:number)=>[string,number];",
                conditionalRelations: "function f<T>(){type R0=T extends {a:infer U}?U:never;type R1=T extends {a:infer V}?V:never;type R2=T extends {b:infer U}?U:never;type R3=unknown;}",
                genericCallbacks: "type R0=<T>(cb:(value:T)=>void)=>T;type R1=(cb:(value:string)=>void)=>string;type R2=(cb:(value:number)=>void)=>number;type R3=(cb:(value:'a')=>void)=>string;",
                contextualDefaults: "type R0=<T=string>()=>T;type R1=()=>number;type R2=()=>string;type R3=<T extends string>()=>T;type R4=()=>void;",
            })
        ) add(`inference:relations-${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true, false, false, false, false, true, true);
    }
}
for (const strict of [false, true]) {
    for (const exactOptionalPropertyTypes of [false, true]) {
        for (
            const [name, source] of Object.entries({
                primitive: "type A=string extends string?1:2;type B=number extends string?1:2;type C=any extends string?1:2;type D=never extends string?1:2;type E=unknown extends string?1:2;type F=string extends any?1:2;",
                generic: "type A<T>=T extends string?1:2;type B=A<'a'|1>;type C=A<never>;type D=A<any>;type E=A<unknown>;type F=A<string>;",
                constrained: "type A<T extends string>=T extends 'a'?1:2;type B=A<'a'>;type C=A<'b'>;type D<T extends {a:any}>=T extends {a:string}?1:2;type E=D<{a:number}>;",
                tuples: "type A<T>=[T] extends [string]?1:2;type B=A<'a'|1>;type C=A<never>;type D<T>=[T,string] extends [number,string]?1:2;type E=D<number>;",
                nested: "type A<T>=T extends string?'s':T extends number?'n':T extends boolean?'b':'o';type B=A<string|number|boolean|{}>;type C=A<any>;",
                branches: "type A<T>=T extends string?T:never;type B<T>=T extends string?never:T;type C=A<1|'a'|undefined>;type D=B<1|'a'|undefined>;",
                readonly: "type A<T>=T extends readonly number[]?T[number]:never;type B=A<[1,2]>;type C=A<readonly [3,4]>;type D=A<string[]>;",
                mapper: "type A<T,U>=T extends U?[T,U]:never;type B<T>=A<T,string>;type C=B<'a'|1>;type D=A<string|number,number>;",
                branchAlias: "type Box<T>={value:T};type A<T>=T extends string?Box<T>:Box<number>;type B=A<'a'>;type C=A<boolean>;",
                recursion: "type A<T extends unknown[]>=T['length'] extends 8?T:A<[...T,1]>;type B=A<[]>;type C=A<[1,1]>;",
                tailLimit: "type A<T>=T extends string?A<T>:number;type B=A<'a'>;type C=A<1>;type D=A<never>;",
                objectChecks: "type A<T>={value:T} extends {value:string}?1:2;type B=A<string>;type C=A<number>;type D<T>={value:T} extends {value:any}?1:2;type E<T>=T[] extends string[]?1:2;type F=E<number>;",
                functionChecks: "type A<T>=((value:T)=>void) extends ((value:string)=>void)?1:2;type B=A<string>;type C=A<number>;type D<T>=(()=>T) extends (()=>number)?1:2;type E=D<number>;",
            })
        ) add(`conditional:${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true);
        for (
            const [name, source] of Object.entries({
                branches: "function f<T>(){type R0=T extends string?1:2;type R1=T extends string?1:2;type R2=T extends string?string:number;type R3=1|2;type R4=number;}",
                filtering: "type Exclude<T,U>=T extends U?never:T;function f<T extends string|number>(){type R0=Exclude<T,string>;type R1=number;type R2=T;type R3=Exclude<T,number>;type R4=string;}",
                tupleChecks: "function f<T>(){type R0=[T] extends [string]?1:2;type R1=T extends string?1:2;type R2=1|2;type R3=1;type R4=2;}",
                anyBranch: "function f<T>(){type R0=T extends string?any:number;type R1=T extends string?string:any;type R2=number;type R3=string;type R4=unknown;}",
                dependent: "function f<T>(){type R0=T extends string?T:number;type R1=T extends string?T:number;type R2=string|number;type R3=T;type R4=T extends number?string:T;}",
                variance: "type C<T>=T extends string?{a:T}:{b:T};type R0={value:C<'a'>};type R1={value:C<string>};type R2={value:C<number>};function f<T,U extends T>(){type R3={value:C<T>};type R4={value:C<U>};}",
            })
        ) add(`conditional:relations-${name}:${strict}:${exactOptionalPropertyTypes}`, { "globals.d.ts": library, "main.ts": source }, { strict, exactOptionalPropertyTypes }, false, true, false, false, false, false, true, true);
    }
}
for (const target of ["es2015", "es2022"]) {
    for (
        const [name, source] of Object.entries({
            before: "__use(x); let x = 1; __use(x);",
            self: "let x = __use(x); var y = __use(y); const z = __use(z);",
            destructuring: "let [a = __use(b), b = __use(b), c = __use(a)] = []; let [d] = __use(d);",
            nestedBinding: "let [{a = __use(b), b = 1}, c = __use(a)] = [];",
            loops: "for (let x of __use(x)) {} for (let y in __use(y)) {} for (let z = __use(z);;) {}",
            deferred: "function f() { __use(x); } let x = 1; const arrow = () => __use(y); let y = 1;",
            immediate: "(() => __use(x))(); let x = 1; (function() { __use(y); })(); let y = 1;",
            asyncImmediate: "let x = (async () => __use(x))(); let y = (function*() { __use(y); })();",
            parameter: "function f(a = __use(a), b = __use(c), c = __use(a)) {}",
            parameterDeferred: "function f(a = () => __use(a), b = () => __use(c), c = 1) {}",
            parameterBinding: "function f([a = __use(b), b = __use(a)] = []) {}",
            ambient: "__use(x); declare let x: number; __use(C); declare class C {}",
            classes: "__use(C); class C { [__use(C)]() {} static x = __use(C); } __use(C);",
            classDeferred: "class C { x = __use(D); static y = __use(D); method() { __use(D); } } class D {}",
            staticBlocks: "class C { static { __use(D); } } class D {} class E { static { __use(D); } }",
            enums: "__use(E); enum E { A } __use(E); __use(F); const enum F { A }",
            enumIsolated: "__use(E); const enum E { A }",
            shadowed: "let x = 1; { __use(x); let x = 2; } __use(x);",
            exports: "__use(x); export let x = 1; __use(x);",
            missing: "__use(missing); __use(console); __use(document); __use(Buffer); __use(test); __use($); __use(Bun);",
            assignments: "let a, b, rest; a = 1; a += 2; a ||= 3; a ??= 4; a++; --b; [a, ...rest] = []; ({a, b: a, ...rest} = {}); for (a of []) {} for (b in {}) {} (a!) = 1;",
            expressionContexts: "let x: string; x as string; x satisfies string; typeof x; void x; if(x) x; while(x) x; do { x; } while(x); switch(x) { case x: break; default: x; } for(x; x; x) x; try { throw x; } catch (e) {} const t = `${x}`; const o = {x, [x]:x}; type Q = typeof x;",
            typeContexts: "interface I { [x]: number; p: typeof x; } type T = { [x]: number }; declare const x: unique symbol; abstract class C implements I { abstract [x]: number; p: typeof x; }",
            decorators: "@dec(__use(C)) class C { @dec(__use(C)) x; @dec(() => __use(C)) y; @dec((() => __use(C))()) z; @dec(__use(C)) method(@dec(__use(C)) x) {} }",
            properties: "class C { a = __order(this.a); b = __order(this.c); c = 1; d = __order(this.c); e = () => __order(this.e); f = __order(this.g); g; h = __order(this.i); i!; }",
            staticProperties: "class C { static a = __order(C.b); static b = 1; static c = __order(C.b); static d = __order(C.method); static method() {} }",
            parameterProperties: "class C { a = __order(this.p); constructor(public p: number, readonly q = __order(this.p)) {} b = __order(this.q); }",
            constructorScope: "let x = 1; class C { p = __use(x); constructor() { let x = 2; } }",
        })
    ) add(`references:${name}:${target}`, { "globals.d.ts": library, "main.ts": source }, { target, isolatedModules: name === "enumIsolated" });
    add(`references:type-import:${target}`, { "globals.d.ts": library, "a.ts": "export class C {}", "b.ts": "import type { C } from './a'; __use(C);" }, { target, module: "esnext" });
    add(`references:type-export:${target}`, { "globals.d.ts": library, "a.ts": "export class C {}", "b.ts": "export type { C } from './a';", "c.ts": "import { C } from './b'; __use(C);" }, { target, module: "esnext" });
    for (const allowUmdGlobalAccess of [false, true]) add(`references:umd:${target}:${allowUmdGlobalAccess}`, { "globals.d.ts": library, "umd.d.ts": "export as namespace U; export const value: number;", "main.ts": "export {}; __use(U);" }, { target, allowUmdGlobalAccess, module: "esnext" });
    add(`references:isolated-global:${target}`, { "globals.d.ts": library + " declare const Collision: number;", "a.ts": "export interface Collision {}", "b.ts": "import { Collision } from './a'; __use(Collision);" }, { target, isolatedModules: true, module: "esnext" });
    add(`references:legacy-decorators:${target}`, { "globals.d.ts": library, "main.ts": "@dec(__use(C)) class C { @dec(__use(C)) x; @dec(__use(C)) method(@dec(__use(C)) x) {} }" }, { target, experimentalDecorators: true });
    add(`references:cross-file:${target}`, { "globals.d.ts": library, "a.ts": "__use(x);", "b.ts": "let x = 1;" }, { target });
}
for (const input of cases.filter(c => c.name.startsWith("references:"))) input.references = true;

for (const strict of [false, true]) {
    for (
        const [name, source] of Object.entries({
            straight: "function f(x: string | number) { __flow(x); x = 1; __flow(x); x = 'a'; __flow(x); }",
            branches: "function f(x: string | number, condition: boolean) { if (condition) x = 1; else x = 'a'; __flow(x); if (condition) x = 2; __flow(x); }",
            returns: "function f(x: string | number, condition: boolean) { if(condition) { x = 1; return __flow(x); } x = 'a'; __flow(x); }",
            loop: "function f(x: string | number, condition: boolean) { while(condition) { __flow(x); x = 1; } __flow(x); }",
            nestedLoops: "function f(x: string | number, a: boolean, b: boolean) { while(a) { while(b) { __flow(x); x = 1; } x = 'a'; } __flow(x); }",
            finally: "function f(x: string | number, condition: boolean) { try { x = 1; if(condition) return __flow(x); } finally { __flow(x); x = 'a'; } __flow(x); }",
            truthiness: "function f(x: string | null | undefined) { if(x) { __flow(x); } else { __flow(x); } }",
            typeof: "function f(x: string | number | boolean | undefined) { if(typeof x === 'string') __flow(x); else __flow(x); }",
            equality: "function f(x: 'a' | 'b' | 1 | undefined) { if(x === 'a') __flow(x); else __flow(x); if(x != null) __flow(x); }",
            numbers: "function f(x: 1 | 2 | 3 | string) { if(x === 2) __flow(x); else __flow(x); if(x != 1) __flow(x); }",
            genericPredicate: "declare function isString(value: unknown): value is string; function f<T>(x: T) { if(isString(x)) __flow(x); else __flow(x); }",
            genericTypeof: "function f<T extends string | number | undefined>(x: T) { if(typeof x === 'string') __flow(x); else __flow(x); if(x !== undefined) __flow(x); }",
            genericAssert: "declare function assertString(value: unknown): asserts value is string; function f<T>(x: T) { assertString(x); __flow(x); }",
            enumGuards: "enum E { A=1, B=2 } function f(x: E | 'a') { if(x === 1) __flow(x); else __flow(x); }",

            nullable: "function f(x: string | number | null | undefined) { if(x === null) __flow(x); else __flow(x); if(x === undefined) __flow(x); else __flow(x); }",
            coercion: "function f(x: string | number | boolean) { if(x == 1) __flow(x); else __flow(x); if(x == true) __flow(x); }",
            typeofAll: "function f(x: unknown) { if(typeof x === 'object') __flow(x); if(typeof x !== 'function') __flow(x); if(typeof x === 'bigint') __flow(x); if(typeof x === 'symbol') __flow(x); if(typeof x === 'host') __flow(x); }",
            unknownEquality: "function f(x: unknown) { if(x === 1) __flow(x); else __flow(x); if(x == 'a') __flow(x); }",
            booleanLiterals: "function f(x: boolean | number) { x = true; __flow(x); x = false; __flow(x); if(x === true) __flow(x); else __flow(x); }",
            guards: "function f(x: string | number | undefined) { if(typeof x === 'string' || x === undefined) __flow(x); else __flow(x); if(x && typeof x !== 'number') __flow(x); }",
            aliased: "function f(x: string | number) { const isString = typeof x === 'string'; if(isString) __flow(x); else __flow(x); }",
            aliasedChain: "function f(x: string | number) { const a = typeof x === 'string'; const b = a; const c = b; const d = c; const e = d; const g = e; if(e) __flow(x); if(g) __flow(x); }",
            aliasedAssigned: "function f(x: string | number) { const a = typeof x === 'string'; x = 1; if(a) __flow(x); }",
            loopGuards: "function f(x: string | number, condition: boolean) { while(typeof x === 'string') { __flow(x); if(condition) { x = 1; continue; } x = 'a'; } __flow(x); }",
            doLoop: "function f(x: string | number, c: boolean) { do { __flow(x); x = 1; } while(c); __flow(x); }",
            labeled: "function f(x: string | number, a: boolean, b: boolean) { outer: while(a) { x = 1; while(b) { if(a) break outer; x = 'a'; } __flow(x); } __flow(x); }",
            catchFinally: "function f(x: string | number, c: boolean) { try { if(c) { x = 1; throw 1; } x = 'a'; } catch(e) { __flow(x); x = 2; } finally { __flow(x); } __flow(x); }",
            evolving: "function f() { let x; x = []; __flow(x); x.push(1); __flow(x); x.unshift('a'); __flow(x); }",
            evolvingBranches: "function f(c: boolean) { let x; if(c) { x = []; x.push(1); } else { x = []; x.push('a'); } __flow(x); }",
            evolvingLoop: "function f(c: boolean) { let x; x = []; while(c) { x.push(1); __flow(x); } __flow(x); }",
            evolvingIndex: "function f() { let x; x = []; x[0] = 1; __flow(x); x['key'] = 'a'; __flow(x); }",
            assertions: "declare function assert(value: unknown): asserts value; function f(x: string | undefined) { assert(x); __flow(x); }",
            assertionNever: "declare function assert(value: unknown): asserts value; function f(x: string | number) { assert(false || false); x = 1; __flow(x); }",
            predicate: "declare function isString(value: unknown): value is string; function f(x: string | number) { if(isString(x)) __flow(x); else __flow(x); }",
            assertType: "declare function assertString(value: unknown): asserts value is string; function f(x: string | number) { assertString(x); __flow(x); }",
            neverCall: "declare function fail(): never; function f(x: string | number) { fail(); x = 1; __flow(x); }",
            objectPredicate: "interface A { a: number } interface B { b: string } declare function isA(value: unknown): value is A; function f(x: A | B) { if(isA(x)) __flow(x); else __flow(x); }",
            switchLiterals: "function f(x: 'a' | 'b' | 1) { switch(x) { case 'a': __flow(x); break; case 'b': __flow(x); break; default: __flow(x); } __flow(x); }",
            switchFallthrough: "function f(x: 'a' | 'b' | 'c') { switch(x) { case 'a': case 'b': __flow(x); break; case 'c': __flow(x); break; } __flow(x); }",
            switchExhaustive: "function f(x: 'a' | 'b') { switch(x) { case 'a': return; case 'b': return; } __flow(x); }",
            switchTypeof: "function f(x: string | number | boolean) { switch(typeof x) { case 'string': __flow(x); break; case 'number': __flow(x); break; default: __flow(x); } }",
            switchTypeofExhaustive: "function f(x: string | number) { switch(typeof x) { case 'string': return; case 'number': return; } __flow(x); }",
            switchTypeofDuplicate: "function f(x: unknown) { switch(typeof x) { case 'string': __flow(x); break; case 'string': __flow(x); break; case 'host': __flow(x); break; default: __flow(x); } }",
            switchTrue: "function f(x: string | number | undefined) { switch(true) { case typeof x === 'string': __flow(x); break; case x === undefined: __flow(x); break; default: __flow(x); } }",
            switchTrueDefault: "function f(x: string | number | boolean) { switch(true) { case typeof x === 'string': __flow(x); default: __flow(x); break; case typeof x === 'number': __flow(x); } }",
            switchUnknown: "function f(x: unknown) { switch(x) { case 1: __flow(x); break; case 'a': __flow(x); break; default: __flow(x); } }",
            discriminant: "function f(x: {kind:'a', a:number} | {kind:'b', b:string}) { if(x.kind === 'a') __flow(x); else __flow(x); }",
            discriminantAlias: "function f(x: {kind:'a', a:number} | {kind:'b', b:string}) { const kind = x.kind; if(kind === 'a') __flow(x); else __flow(x); }",
            discriminantSwitch: "function f(x: {kind:'a', a:number} | {kind:'b', b:string}) { switch(x.kind) { case 'a': __flow(x); break; default: __flow(x); } }",
            optionalDiscriminant: "function f(x: {kind:'a', a:number} | {kind:'b', b:string} | undefined) { if(x?.kind === 'a') __flow(x); else __flow(x); }",
            booleanComparison: "function f(x: string | number) { if((typeof x === 'string') === true) __flow(x); else __flow(x); }",
            lastAssignments: "export {}; function f(x: string | number, c: boolean) { let y: string | number; y = 1; if(c) { y = 'a'; } const h = () => { x = 1; }; __flow(x); } let local = 1; export {local};",
        })
    ) {
        add(`flow:${name}:${strict}`, { "globals.d.ts": library + " declare function __flow(value: unknown): void;", "main.ts": source }, { strict }, false, true);
    }
}
for (const strict of [false, true]) {
    const variants = Array.from({ length: 12 }, (_, i) => `{kind:'k${i}',value:${i}}`).join(" | ");
    add(`flow:large-discriminant:${strict}`, { "globals.d.ts": library + " declare function __flow(value: unknown): void;", "main.ts": `type Item = ${variants}; function f(x: Item) { if(x.kind === 'k3') __flow(x); else __flow(x); switch(x.kind) { case 'k2': case 'k5': __flow(x); break; default: __flow(x); } }` }, { strict }, false, true);
}
for (const input of cases.filter(c => c.name.startsWith("flow:"))) input.flow = true;

for (const strict of [false, true]) {
    for (
        const [name, source] of Object.entries({
            variables: "let x: string | number; __expr(x); x = 1; __expr(x); x = 'a'; __expr(x);",
            local: "function f(c: boolean) { let x: number; __expr(x); if(c) x = 1; __expr(x); x = 2; __expr(x); }",
            definite: "function f() { let x!: number; __expr(x); }",
            literals: "const x = 1; let y = 'a'; __expr(x); __expr(y);",
            before: "__expr(x); let x = 1; function f(a = a, b = c, c = 1) { __expr(a); __expr(b); }",
            narrowing: "function f(x: string | number | undefined) { if(typeof x === 'string') __expr(x); else __expr(x); if(x !== undefined) __expr(x); }",
            branches: "function f(c: boolean) { let x: string | number; if(c) x = 1; else x = 'a'; __expr(x); }",
            unassignedClosure: "function f() { let x: number; function g() { __expr(x); } }",
            captured: "function f(x: string | number) { if(typeof x === 'string') { const g = () => { __expr(x); }; } }",
            mutableCapture: "function f(x: string | number) { x = 1; const g = () => { __expr(x); }; x = 'a'; }",
            parameterDefault: "function f(x: string | undefined = 'a', y: number | undefined = undefined) { __expr(x); __expr(y); }",
            automatic: "function f(c: boolean) { let x; __expr(x); x = 1; __expr(x); if(c) x = 'a'; __expr(x); }",
            nonNullAuto: "function f() { let x; __expr(x!); x = 1; __expr(x!); }",
            generic: "function f<T extends string | number | undefined>(x: T) { if(typeof x === 'string') __expr(x); else __expr(x); }",
            arguments: "function f() { __expr(arguments); const g = () => { __expr(arguments); }; }",
            invalidArguments: "class C { x = __expr(arguments); static { __expr(arguments); } }",
            unknown: "__expr(missing); __expr(undefined);",
            typeQuery: "const x = 1; let y: typeof x; __expr(y); declare function f(x: number): string; type F = typeof f; __expr(f);",
            circularAnnotation: "let x: typeof x; __expr(x); let y: typeof z; let z: typeof y; __expr(y); __expr(z);",
            assignment: "function f(x: string | number) { __expr(x = 1); __expr(x); __expr(x = 'a'); __expr(x); __expr(x += 'b'); __expr(x); }",
            invalidAssignment: "let x: number; __expr(x = 'a'); const y = 1; __expr(y = 2); function f(): void {} __expr(f = 1); __expr(undefined = 1);",
            increments: "function f(x: number, y: bigint, z: string) { __expr(x++); __expr(--y); __expr(z++); } const n = 1; __expr(n++);",
            compoundLike: "function f(x: 1 | 2) { __expr(x = x + 1); __expr(x); __expr(x *= 2); }",
            logicalAssignments: "function f(x: string | undefined, y: number | undefined) { __expr(x ??= 'a'); __expr(x); __expr(y ||= 1); __expr(y); }",
            parameterCycle: "function f(x: string | undefined = x) { __expr(x); }",
            prefixSuggestion: "class C { value: number; static count: number; method() { __expr(value); __expr(count); } }",
            deprecated: "/** @deprecated */ const old = 1; __expr(old); /** @deprecated */ function oldFunction(): void {} __expr(oldFunction);",
        })
    ) add(`identifiers:${name}:${strict}`, { "globals.d.ts": library + " declare function __expr(value: unknown): void;", "main.ts": source }, { strict }, false, true);
}
for (const strict of [false, true]) {
    for (const verbatimModuleSyntax of [false, true]) {
        const base = { "globals.d.ts": library + " declare function __expr(value: unknown): void;" };
        add(`identifiers:imports:${strict}:${verbatimModuleSyntax}`, { ...base, "a.ts": "export const value = 1; export declare function f(): number;", "b.ts": "import {value, f} from './a'; __expr(value); __expr(f); __expr(value = 2);" }, { strict, verbatimModuleSyntax, module: "esnext" }, false, true);
        add(`identifiers:type-import:${strict}:${verbatimModuleSyntax}`, { ...base, "a.ts": "export const value = 1;", "b.ts": "import type {value} from './a'; __expr(value);" }, { strict, verbatimModuleSyntax, module: "esnext" }, false, true);
        add(`identifiers:deprecated-import:${strict}:${verbatimModuleSyntax}`, { ...base, "a.ts": "/** @deprecated */ export declare function old(): number;", "b.ts": "import {old} from './a'; __expr(old);" }, { strict, verbatimModuleSyntax, module: "esnext" }, false, true);
        add(`identifiers:alias-chain:${strict}:${verbatimModuleSyntax}`, { ...base, "main.ts": "namespace N { export class C {} } import A = N; import B = A.C; __expr(B);" }, { strict, verbatimModuleSyntax }, false, true);
    }
    for (
        const [name, text] of Object.entries({
            callHint: "declare function value(): number; let x: number; __expr(x = value);",
            constructHint: "interface NumberBox {new(): number} declare const Box: NumberBox; let x: number; __expr(x = Box);",
            scopeArguments: "function outer() { class C { value = __expr(arguments); static { __expr(arguments); } } }",
            catchVariable: "function f() { try {} catch(e) { __expr(e); } }",
            outerNeverInitialized: "function f(c: boolean) { let x: number; const g = () => { __expr(x); }; if(c) { const h = () => { __expr(x); }; } }",
            genericContext: "function f<T extends string | undefined>(x: T) { if(x) { const y: string = x; __expr(x); } }",
        })
    ) add(`identifiers:${name}:${strict}`, { "globals.d.ts": library + " declare function __expr(value: unknown): void;", "main.ts": text }, { strict }, false, true);
}
for (const input of cases.filter(c => c.name.startsWith("identifiers:"))) input.identifiers = true;

let selected = process.argv.includes("--identifiers") ? cases.filter(c => c.identifiers) : process.argv.includes("--flow") ? cases.filter(c => c.flow) : process.argv.includes("--references") ? cases.filter(c => c.references) : process.argv.includes("--awaited") ? cases.filter(c => c.awaited) : process.argv.includes("--binary") ? cases.filter(c => c.name.startsWith("binary:")) : process.argv.includes("--initializers") ? cases.filter(c => c.name.startsWith("initializers:")) : process.argv.includes("--expressions") ? cases.filter(c => c.expressions && !c.name.startsWith("binary:") && !c.name.startsWith("awaited:")) : process.argv.includes("--constants") ? cases.filter(c => c.name.startsWith("constants:")) : process.argv.includes("--inference") ? cases.filter(c => c.typeNodes && c.name.startsWith("inference:")) : process.argv.includes("--conditional") ? cases.filter(c => c.typeNodes && c.name.startsWith("conditional:")) :
    process.argv.includes("--generic-relations") ? cases.filter(c => c.genericRelations) :
    process.argv.includes("--indexing") ? cases.filter(c => c.name.startsWith("indexing:")) :
    process.argv.includes("--assignability") ? cases.filter(c => c.assignability && !c.genericRelations && !c.name.startsWith("conditional:") && !c.name.startsWith("inference:") && !c.name.startsWith("constants:") && !c.name.startsWith("expressions:") && !c.name.startsWith("binary:") && !c.name.startsWith("awaited:")) :
    process.argv.includes("--identity") ? cases.filter(c => c.identity && !c.assignability) : process.argv.includes("--signatures") ? cases.filter(c => c.signatures) : process.argv.includes("--properties") ? cases.filter(c => c.properties) : process.argv.includes("--values") ? cases.filter(c => c.values && !c.name.startsWith("initializers:")) : process.argv.includes("--members") ? cases.filter(c => c.members && !c.values && !c.signatures) : process.argv.includes("--type-nodes") ? cases.filter(c => c.typeNodes && !c.identifiers && !c.flow && !c.members && !c.properties && !c.identity && !c.name.startsWith("indexing:") && !c.name.startsWith("conditional:") && !c.name.startsWith("inference:") && !c.name.startsWith("constants:") && !c.name.startsWith("expressions:") && !c.name.startsWith("binary:") && !c.name.startsWith("awaited:"))
    : cases.filter(c => !c.references && !c.typeNodes && Boolean(c.aliases) === process.argv.includes("--aliases"));
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
    scope: process.argv.includes("--identifiers") ? "Identifier expression types, definite assignment, captured flow, parameter defaults and generic reference constraints; full checker integration remains incomplete" :
        process.argv.includes("--flow") ? "Control-flow types, assignment reduction, branch and loop joins and narrowing; full checker integration remains incomplete" : process.argv.includes("--references") ? "Value-name resolution, declaration order, parameter initialization and type-only alias diagnostics" : process.argv.includes("--binary") ? "Binary expression result types, operator diagnostics and nullish semantics with required assignment/access services" : process.argv.includes("--awaited") ? "Promise and thenable fulfillment, awaited types, recursion and generic wrappers; classification queries make lazy generic metadata deterministic" : process.argv.includes("--expressions") ? "Primitive expression types, template evaluation, diagnostics and grammar checks with required advanced expression services" :
        process.argv.includes("--constants") ? "Constant and enum evaluation with provenance, forward references and numeric boundaries" : process.argv.includes("--initializers") ? "Variable, parameter and property initializer types with required flow, binding-pattern and contextual services" : process.argv.includes("--inference") ? "Type inference, constraints, reverse mapped types, widening and contextual signatures; expression inference and full checker integration remain incomplete" : process.argv.includes("--conditional") ? "Conditional evaluation, distribution, tail recursion, constraints and relations with required inference services; full checker integration remains incomplete" : process.argv.includes("--generic-relations") ? "Generic key/indexed/mapped relations, optionality, variance and cache graphs; conditional and full diagnostic services remain incomplete" :
        process.argv.includes("--indexing") ? "Key enumeration, indexed access, read/write simplification and generic cache identity; expression checking and full checker integration remain incomplete" : process.argv.includes("--assignability") ? "Structural relation decisions, signature variance, discriminants and generic variance caches with required advanced semantic services; full checker integration remains incomplete" : process.argv.includes("--identity") ? "Structural identity, primitive relation predicates, normalization and recursive caches with required advanced relation services; full checker integration remains incomplete" : process.argv.includes("--signatures") ? "Signature matching, composition, tuple rest parameters and array member fallback with explicit type relation dependencies; full checker integration remains incomplete" :
        process.argv.includes("--properties") ? "Composite properties, apparent types and intersection reduction with explicit relation and signature dependencies; full checker integration remains incomplete" : process.argv.includes("--values") ? "Source symbol read/write types, accessors, value aliases and declaration value objects with explicit inference dependencies; full checker integration remains incomplete" : process.argv.includes("--members") ? "Source structured members, interface bases, signatures and index signatures with annotated value dependencies; full checker integration remains incomplete" : process.argv.includes("--type-nodes") ? "Source type-node evaluation, declared aliases and references with explicit semantic dependencies; full checker integration remains incomplete" : process.argv.includes("--aliases")
        ? "Program-backed alias targets, type-only chains and module exports with explicit semantic dependencies; full checker integration remains incomplete"
        : "Program-owned global symbols, class/interface headers and generic scopes with explicit semantic dependencies; full checker integration remains incomplete",
    referenceRevision,
    managed,
    runtime: managed ? "managed development run" : await run(candidate, ["--native-check"]),
    cases: selected.length,
    identifierQueries: actual.reduce((count, c) => count + (c.identifierQueries?.length ?? 0), 0),
    assignmentQueries: actual.reduce((count, c) => count + (c.assignmentMarks?.length ?? 0), 0),
    flowQueries: actual.reduce((count, c) => count + (c.flowQueries?.length ?? 0), 0),
    declarationOrderQueries: actual.reduce((count, c) => count + (c.declarationOrder?.length ?? 0), 0),
    referenceSyntaxQueries: actual.reduce((count, c) => count + (c.referenceSyntax?.length ?? 0), 0),
    referenceQueries: actual.reduce((count, c) => count + (c.referenceQueries?.length ?? 0), 0),
    awaitedQueries: actual.reduce((count, c) => count + (c.awaitedQueries?.length ?? 0), 0),
    expressionQueries: actual.reduce((count, c) => count + (c.expressionQueries?.length ?? 0), 0),
    constantQueries: actual.reduce((count, c) => count + (c.constantQueries?.length ?? 0), 0),
    keyQueries: actual.reduce((count, c) => count + (c.keyQueries?.length ?? 0), 0),
    indexQueries: actual.reduce((count, c) => count + (c.indexQueries?.length ?? 0), 0),
    numericStringConversions: selected.reduce((count, c) => count + (c.numberStrings?.length ?? 0), 0),
    relationPairs: actual.reduce((count, c) => count + (c.relations?.length ?? 0), 0),
    relationKeyQueries: actual.reduce((count, c) => count + (c.relationKeys?.length ?? 0), 0),
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
