import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import {
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isDeepStrictEqual } from "node:util";
import { referenceRevision } from "./common.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const symbolLocations = process.argv.includes("--symbols");
const scopeServices = process.argv.includes("--scopes");
const contextQueries = process.argv.includes("--contexts");
const declarationVisibility = process.argv.includes("--visibility");
const symbolChains = process.argv.includes("--chains");
const computedSymbols = process.argv.includes("--computed-symbols");
const symbolFormats = process.argv.includes("--symbol-formats") || computedSymbols;
const symbolDisplay = process.argv.includes("--symbol-display") || symbolFormats;
const accessibility = process.argv.includes("--accessibility") || symbolDisplay;
const output = path.join(root, `built/csharp/checker-${computedSymbols ? "computed-symbols" : symbolFormats ? "symbol-formats" : symbolDisplay ? "symbol-display" : accessibility ? "accessibility" : symbolChains ? "symbol-chains" : declarationVisibility ? "declaration-visibility" : contextQueries ? "context-queries" : scopeServices ? "scope-services" : symbolLocations ? "symbol-locations" : "locations"}`);
const option = name => process.argv[process.argv.indexOf(name) + 1];
const dotnet = process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet";
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const oracle = path.join(root, "built/csharp/checker-program-oracle.exe");
const hash = value => createHash("sha256").update(value).digest("hex");
await mkdir(output, { recursive: true });
const oracleHash = hash(await readFile(oracle));
const candidateHash = hash(Buffer.concat([await readFile(dll), await readFile(path.join(root, "csharp/src/TypeScript.Compiler/bin/Release/net11.0/TypeScript.Compiler.dll"))]));
const library = `interface IArguments {} interface Object {} interface Function {} interface CallableFunction extends Function {}
interface NewableFunction extends Function {} interface String {} interface Number {} interface Boolean {} interface RegExp {}
interface Array<T> { length: number; [n: number]: T; } interface ReadonlyArray<T> { readonly length: number; readonly [n: number]: T; }
interface ThisType<T> {}`;
const fixtures = {
    literals: `let a=1; const b="text"; const c=true; const d=12n; let n=null; let u=undefined; const r=/a/; const t=\`a\${a}\`;`,
    types: `type U = string | number; type I = {x:number}&{y:string}; interface Box<T> { value:T; method(x:T):T; }
declare const x: Box<U>; type Item=typeof x; type Key=keyof Item; type Value=Item["value"];`,
    expressions: `let x: string|number=1; if(typeof x === "number") { x+2; } const o={x, f(a:number){return a+1;}};
o.x; o["x"]; o.f(1); (o.x as number); o satisfies {x:number}; x!; void x; typeof o; delete o.x;`,
    namespaces: `namespace A { export namespace B { export interface C { value:number } export const value=1; } }
import Alias=A.B; let x: A.B.C; Alias.value; type T=typeof A.B.value;`,
    imports: `import value, { named as local, type Shape } from "./dep"; import * as ns from "./dep";
export {local as renamed}; export type {Shape as Exported}; let x:Shape; ns.named; value; local;`,
    typeImports: `import type {Shape as S} from "./dep"; import type Default from "./dep"; export type {Shape as T} from "./dep";
type Imported=import("./dep").Shape; type Values=typeof import("./dep"); let x:S;`,
    typeImportDefault: `import type Default from "./dep"; let x:Default; type Imported=import("./dep").Shape; type Values=typeof import("./dep");`,
    bindings: `declare const source:{a:number,b?:string,nested:{c:boolean}}; const {a: renamed,b="fallback",nested:{c},...rest}=source;
const [first,,third=3,...tail]=[1,2,3]; function f({a}:{a:number},[b]:[string]) { return [a,b]; }`,
    classes: `interface I { id:number; } class Base<T> { value!:T; self():this{return this;} }
class Derived extends Base<string> implements I { id=1; #secret=1; constructor(){super();this.value="";}
method(this:Derived,arg:Base<string>){return arg.value;} get item(){return this.id;} set item(value:number){this.id=value;} }
new Derived().method(new Base<string>());`,
    functions: `function f<T>(x:T):T { return x; } const g=<T>(x:T)=>x; const h=function named(x:number){return x;};
function predicate(x:unknown):x is number{return typeof x === "number";} f(1); g("x"); h(2);`,
    tuples: `type Tuple=[first:number,second?:string,...rest:boolean[]]; type T=readonly [1,"x"]; type F=(x:number)=>string;
type C=new(x:number)=>{x:number}; declare const tuple:Tuple; tuple[0];`,
    mapped: `type Map<T>={readonly [K in keyof T]?:T[K]}; type Pick<T>=T extends {value:infer V}?V:never;
type Names<T extends string>=\`get\${T}\`; let x:Map<{a:number}>;`,
    loops: `let x=0; for(x=1;x<3;x++){ x; } for(const k in {a:1}){k;} for(const v of [1,2]){v;}
while(x){x--;} do{x++;}while(x<2); switch(x){case 1:x;break;default:x;} try{throw x;}catch(e){e;}`,
    withStatement: `declare const obj:any; with(obj){ missing; let x=missing; x(); }`,
    meta: `interface ImportMeta { url:string; } function f(){new.target;} import.meta.url;`,
    enum: `enum E { A=1,B=A+1 } const e=E.A; type EnumType=E; namespace E { export const value=3; } E.value;`,
    importsCommonJs: `import dep=require("./dep"); export=dep;`,
    contextual: `const f:(x:{value:number})=>number=x=>x.value; const a:{method(x:number):number}={method(x){return x;}};
const b=[1,"x"] as const; const c=(1 as const); const named:{x?:number}={x:1};`,
    jsx: `declare namespace JSX { interface Element {} interface IntrinsicElements { div:{title?:string}; } }
function Component(props:{value:number}) {return <div title="x"/>;} const node=<Component value={1}/>;`,
    jsdoc: `/** @template T @param {T} value @returns {T} */ function identity(value){ return value; }
/** @type {number} */ let n=1; const s=identity("text");`,
};
if (symbolLocations) {
    Object.assign(fixtures, {
        jsdocReferences: `class C { field=1; static value=2; }
/** Links {@link value}, {@link C.field} and {@link C.value}.
 * @param {number} value A value.
 * @returns {number} The result.
 */
function documented(value){return value;}
/** @template T
 * @param {T} item
 * @returns {T}
 */
function generic(item){return item;}`,
        indexSymbols: `interface Dictionary { [key:string]:number; } declare const dictionary:Dictionary;
dictionary.value; dictionary.value; dictionary["key"]; type Value=Dictionary["key"];
declare const both: { [key:\`a\${string}\`]:number } & { [key:\`\${string}z\`]:1 }; both.az; both.az;`,
        missingSymbols: `let a:Missing; let b:Missing.Member; let c:Missing.Member; missingValue; unknownObject.property;
import {absent} from "./missing"; type Imported=import("./missing").Type;`,
        privateSymbols: `class C { #value=1; #method(){return this.#value;} has(other:object){return #value in other;} }
declare const c:C; c.#value; class D { #value=2; read(){return this.#value;} }`,
        declarationNames: `class C { ["named"](){return 1;} [42]=true; constructor(){} } const o={ ["text"]:1, [42]:true };
namespace N { export class Item {} } import Alias=N.Item; export=Alias;`,
        moduleQueries: `import {named} from "./dep" with {type:"json"}; export {named} from "./dep";
const module=import("./dep", {with:{type:"json"}}); type T=import("./dep", {with:{type:"json"}}).Shape;`,
    });
}
if (scopeServices) {
    Object.assign(fixtures, {
        scopeShadow: `const outer=1; function f<T>(argument:T){let outer="inner"; {const block=true; (()=>argument)();} return argument;}
namespace N { export const outer=true; export interface Item { value:number; } function inner(){let local=outer;return local;} }`,
        scopeStatic: `class C<T> { field!:T; method<U>(this:C<T>,value:U){const local=value;return ()=>arguments;}
static method<U>(value:U){return value;} } const cls=class Named<T> {method(value:T){return Named;}};
const fn=function self(value:number){return ()=>self(value);};`,
        scopeConditional: `type Choose<T>=T extends infer U?U:T; type M<T>={[K in keyof T]:T[K]};
function nested<T>(value:T){function inner<U>(other:U){return {value,other};} return inner;}`,
        scopeExports: `import {named as local} from "./dep"; export {local as forwarded}; export * from "./dep";
export * as everything from "./dep"; export default class Default { method(){return local;} }
namespace N {export const x=1; export interface I {value:number;} }`,
        symbolTypes: `declare let value:string|number; if(typeof value==="string"){value;} value=1; value;
class C { get property():number{return 1;} set property(value:string|number){} }
declare const c:C; c.property; c.property="text"; c["property"]=1;
declare const optional:{value?:number}; optional.value; optional.value=1; optional?.value;`,
        parameterProperties: `export class C { constructor(public value:string,readonly count=1,protected item?:number){const copy={value};this.value=value;} }
declare const c:C; c.value; c.count;`,
    });
}
if (contextQueries) {
    Object.assign(fixtures, {
        contextGeneric: `declare function choose<T>(value:T, callback:(x:T)=>T):T; choose("a",x=>x);
declare function select<T,K extends keyof T>(object:T,key:K):T[K]; select({left:1,right:"x"},"left");`,
        contextBlocked: `declare function color<T extends "red"|"blue">(value:T):T; color("red"); color("missing");
declare function pair<T>(first:T,second:T):T[]; pair("red","blue");`,
        contextOverloads: `declare function f(value:string):string; declare function f(value:number,extra?:number):number;
f("text"); f(true); f(1,2); f(1,2,3);`,
        contextCallbacks: `declare function map<T,U>(items:T[],callback:(value:T)=>U):U[];
const values:number[]=map([1,2],value=>value+1); const functions:Array<(value:number)=>number>=[value=>value+1];`,
        contextTuples: `const tuple:[number,...string[],boolean]=[1,...["x"],true]; const array:number[]=[1,2];
declare function tupleCall(...values:[number,string?,...boolean[]]):void; tupleCall(1,"x",true);`,
        contextTemplates: `interface TemplateStringsArray extends ReadonlyArray<string> { readonly raw:readonly string[]; }
declare function tag<T>(strings:TemplateStringsArray,value:T):T; tag\`text \${1}\`;`,
        jsxContexts: `declare namespace JSX { interface Element {} interface IntrinsicElements {div:{title?:string};} }
function Component(props:{value:"yes"|"no",onChange:(value:number)=>void}){return <div/>;}
const element=<Component value="yes" onChange={value=>{value;}}/>;`,
        contextImports: `interface ImportCallOptions {with?:{type:string};} const module=import("./dep",{with:{type:"json"}});`,
    });
}
if (declarationVisibility || accessibility) {
    Object.assign(fixtures, {
        visibilityExports: `const hidden=1; export const visible=hidden; class Hidden {} export class Public { private secret=1; protected member=1;
public field=1; #private=1; constructor(value:number){} get property(){return this.field;} set property(value:number){this.field=value;} }
export {hidden}; export default Hidden;`,
        visibilityAliases: `namespace N { export class C {} } import A=N; import B=A; export {B};
import Default,{named as value,Shape} from "./dep"; import * as All from "./dep"; export {value,All};`,
        visibilityAmbient: `declare namespace N { interface I {value:number;} class C {private field:number; public method(x:number):void;} }
declare module "external" { class C {} import Alias=N; module "nested" {interface I {}} }`,
        visibilityAugmentation: `export {}; declare module "./dep" { interface Shape { extra:string; } }
declare global { interface Global {value:number;} }`,
        visibilityDts: `export {}; interface Hidden {value:number;} export interface Public {value:Hidden; method(x:number):string;}
declare const hidden:Hidden; export {hidden};`,
        visibilityBindings: `const {a,b:{c}}={a:1,b:{c:2}}; const [first,,...rest]=[1,2,3]; const {}={}; const []=[];
export {a,c,first,rest}; function f({local}:{local:number}){return local;}`,
        jsdocVisibilityCommonJs: `const local=1; function C(){} exports.value=local; module.exports.C=C;
const {named}=require("./dep"); exports.named=named;`,
        jsdocVisibilityTypes: `exports.value=1;
/** @typedef {{value:number}} Shape */
/** @callback Callback
 * @param {Shape} value
 * @returns {number}
 */
function f(value){return value;}
class C { /** @private */ secret=1; /** @protected */ field=1; public=1; }`,
    });
}
if (symbolChains || accessibility) {
    Object.assign(fixtures, {
        chainAliases: `import * as Long from "./dep"; import {Deep} from "./dep"; import Root=require("./dep");
import Local=Deep; import Short=Deep.Member; let value:Deep.Member; function f(Short:number){let value:Deep.Member;}`,
        chainShadow: `namespace N {export class Member{} export const value=1;} import A=N; import B=N;
function f(N:number,A:string){let value:B.Member;} function g(globalValue:string){globalThis.globalValue;}`,
        chainClassNames: `const outer=class Self<T>{method(value:Self<T>){const inner=class Inner<U>{value!:Self<T>;};return inner;}};`,
        chainReexports: `import * as All from "./dep"; export * as Forward from "./dep"; export {named as Alias} from "./dep";
namespace N {export import Internal=All.Deep;} import Internal=N.Internal; let member:Internal.Member;`,
        chainCycles: `import A=B; import B=A; namespace N {export import C=D; export import D=C;} export {A,B};`,
        chainMerged: `interface Shared {value:number;} namespace Shared {export const value=1;} const c:Shared={value:1};
namespace N {export interface T{a:number;}} namespace N {export interface T{b:string;}} import Alias=N; let merged:Alias.T;`,
        chainTypeOnly: `import type {Shape} from "./dep"; import type * as Types from "./dep"; import type Alias=Types.Deep;
function f<Shape>(value:Shape){let other:Types.Shape;} let member:Alias.Member;`,
        chainDeepReexports: `import * as Surface from "./barrel2"; import Root=require("./barrel2");
let value=Surface.next.nested.named; function shadow(next:number,nested:number){return Surface.next.nested.named;}`,
    });
}
if (accessibility) {
    Object.assign(fixtures, {
        accessibilityObjects: `const value={nested:{field:1}}; declare const typed:{property:string}; export type Result=typeof value.nested;
function local(){const hidden={field:1};return hidden;}`,
        accessibilityInstances: `interface Constants {readonly key:unique symbol; method():number;} declare const SymbolLike:Constants;
export type Key=typeof SymbolLike.key; namespace N {export interface I {field:number;} } declare const instance:N.I;`,
        accessibilityExportEquals: `import Root=require("./exported"); export const value:Root.Item=new Root.Item();`,
    });
}
if (symbolDisplay) {
    Object.assign(fixtures, {
        displayNames: `namespace Names { export class C { "with space"=1; 'é'=2; ["computed"]=3; [-1]=4; 0x10=5; #private=6; }
export enum E { "some-key", "中", Numeric=0x10 } } const anonymous=class { "line\\n"=1; }; const fn=()=>1;
export default function Named() { return Names.C; }`,
        displayComputed: `declare const key:unique symbol; export class Outer { [key]=1; ["a-b"]=2; [1e3]=3; }
export namespace Nested { export const literal={ "nul\\0":1, "slash\\\\name":2 }; }
const other=function(){}; const missing=class {}; export {other,missing};`,
        displayAssigned: `const values={"with space":function(){}, method:class {}, ["computed"]:()=>1};
let assigned:any; assigned=function(){}; assigned.member=class {}; assigned["element"]=()=>1; export {values,assigned};`,
        jsdocDisplayNames: `function C(){this["field-name"]=1;this.named=2;} C.prototype["method-name"]=function(){};
exports["exported-name"]=C; Object.defineProperty(exports,"defined-name",{value:C}); const values={"a-b":function(){}};`,
    });
}
const inputs = [];
for (const [name, source] of Object.entries(fixtures)) {
    for (const strict of [false, true]) {
        for (const concurrency of [1, 4]) {
            const files = { "/project/globals.d.ts": library + (symbolChains || accessibility ? "declare const globalValue:number;" : ""), [`/project/main.${name === "visibilityDts" ? "d.ts" : name.startsWith("jsx") ? "tsx" : name.startsWith("jsdoc") ? "js" : "ts"}`]: source, "/project/dep.ts": "export interface Shape {value:number;} export const named=1; export default class Default { value=1; }" + (symbolChains || accessibility ? " export namespace Deep {export class Member {value=1;}}" : "") };
            if (name === "chainDeepReexports") {
                files["/project/barrel.ts"] = "export * as nested from './dep'; export * as loop from './barrel2';";
                files["/project/barrel2.ts"] = "export * as next from './barrel';";
            }
            if (name === "accessibilityExportEquals") files["/project/exported.ts"] = "class Root {} namespace Root {export class Item {value=1;}} export=Root;";
            inputs.push({ name: `${name}:${strict}:${concurrency}`, files: Object.fromEntries(Object.entries(files).map(([name, text]) => [name, Buffer.from(text).toString("base64")])), roots: Object.keys(files), options: { strict, target: "esnext", module: "esnext", moduleResolution: "bundler", jsx: "preserve", ...name.startsWith("jsdoc") ? { allowJs: true, checkJs: true } : {} }, typeNodes: true, ...symbolFormats ? { symbolFormats: true, ...computedSymbols ? { symbolFormatFlags: [20, 22, 28, 30, 52, 54, 60, 62] } : {} } : symbolDisplay ? { symbolDisplay: true } : accessibility ? { accessibility: true } : symbolChains ? { symbolChains: true } : declarationVisibility ? { declarationVisibility: true } : contextQueries ? { contextQueries: true } : scopeServices ? { scopeServices: true } : symbolLocations ? { symbolLocations: true, ...name.startsWith("jsdoc") ? { documentationSymbols: true } : {} } : { locations: true }, concurrency });
        }
    }
}
const formatFixtures = new Set(["namespaces", "imports", "typeImports", "classes", "functions", "importsCommonJs", "visibilityAliases", "visibilityAmbient", "visibilityExports", "chainShadow", "chainReexports", "chainTypeOnly", "chainClassNames", "displayNames", "displayComputed", "displayAssigned", "accessibilityExportEquals", "jsdocDisplayNames"]);
const computedFixtures = new Set(["displayNames", "displayComputed", "displayAssigned", "jsdocDisplayNames", "classes", "visibilityExports", "accessibilityInstances", "chainClassNames"]);
const eligible = computedSymbols ? inputs.filter(input => computedFixtures.has(input.name.split(":")[0])) : symbolFormats ? inputs.filter(input => formatFixtures.has(input.name.split(":")[0])) : inputs;
const selected = process.argv.includes("--filter") ? eligible.filter(input => input.name.includes(option("--filter"))) : eligible;
await writeFile(path.join(output, "inputs.json"), JSON.stringify(selected, null, 2));
if (process.argv.includes("--list")) process.exit(0);
async function probe(command, args, input, executableHash, role) {
    const cache = path.join(output, `${role}-${hash(JSON.stringify(input) + executableHash)}.json`);
    try {
        return JSON.parse(await readFile(cache, "utf8"));
    }
    catch (error) {
        if (error.code !== "ENOENT") throw error;
    }
    let previousError;
    try {
        previousError = await readFile(cache + ".error.txt", "utf8");
    }
    catch (error) {
        if (error.code !== "ENOENT") throw error;
    }
    if (previousError) throw Error(previousError);
    const result = await new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const stdout = [], stderr = [];
        child.stdout.on("data", data => stdout.push(data));
        child.stderr.on("data", data => stderr.push(data));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code =>
            code ? reject(Error(`${role} ${input.name}: ${Buffer.concat(stderr)}`))
                : resolve(JSON.parse(Buffer.concat(stdout).toString())));
        child.stdin.end(JSON.stringify(input) + "\n");
    }).catch(async error => {
        await writeFile(cache + ".error.txt", error.message);
        throw error;
    });
    await writeFile(cache, JSON.stringify(result));
    return result;
}
const failures = [];
const locationMetadataDifferences = [];
let queries = 0, comparedQueries = 0, referenceFailures = 0, candidateFailures = 0;
const results = [];
for (const input of selected) {
    let reference, candidate, referenceError, candidateError;
    try {
        reference = await probe(oracle, [], input, oracleHash, "reference");
    }
    catch (error) {
        referenceError = String(error);
        referenceFailures++;
    }
    try {
        candidate = await probe(dotnet, [dll, "--checker-program-lines"], input, candidateHash, "candidate");
    }
    catch (error) {
        candidateError = String(error);
        candidateFailures++;
    }
    if (candidate) queries += (candidate.symbolFormatQueries ?? candidate.symbolDisplayQueries ?? candidate.accessibilityQueries ?? candidate.symbolChainQueries ?? candidate.visibilityQueries ?? candidate.contextQueries ?? candidate.serviceQueries ?? candidate.symbolLocationQueries ?? candidate.locationQueries).length + (candidate.documentationSymbolQueries?.length ?? 0);
    results.push({ input, reference, candidate, referenceError, candidateError });
    if (referenceError || candidateError) {
        failures.push({ name: input.name, referenceError, candidateError });
        continue;
    }
    comparedQueries += (candidate.symbolFormatQueries ?? candidate.symbolDisplayQueries ?? candidate.accessibilityQueries ?? candidate.symbolChainQueries ?? candidate.visibilityQueries ?? candidate.contextQueries ?? candidate.serviceQueries ?? candidate.symbolLocationQueries ?? candidate.locationQueries).length + (candidate.documentationSymbolQueries?.length ?? 0);
    try {
        assert.deepStrictEqual(candidate, reference);
    }
    catch {
        failures.push({ name: input.name, input, reference, candidate });
        if (symbolLocations) {
            // These columns describe the input comment node, not a returned
            // symbol. Retain every strict failure while reporting answer parity.
            const answers = result =>
                result.documentationSymbolQueries ? {
                    ...result,
                    documentationSymbolQueries: result.documentationSymbolQueries.map(([owner, kind, , , symbol]) => [owner, kind, symbol]),
                } : result;
            if (isDeepStrictEqual(answers(candidate), answers(reference))) locationMetadataDifferences.push(input.name);
        }
    }
}
await writeFile(path.join(output, "failures.json"), JSON.stringify(failures, null, 2));
await writeFile(path.join(output, "results.json"), JSON.stringify(results));
const summary = { configurations: selected.length, queries, comparedQueries, exact: selected.length - failures.length, failed: failures.length, referenceRevision, referenceFailures, candidateFailures, oracleHash, candidateHash, inputHash: hash(JSON.stringify(selected)), resultHash: hash(JSON.stringify(results)), managed: true };
if (symbolLocations) Object.assign(summary, { symbolAnswerMatches: summary.exact + locationMetadataDifferences.length, locationMetadataDifferences });
if (scopeServices) {
    const counts = Array(7).fill(0);
    for (const result of results) for (const record of result.candidate?.serviceQueries ?? []) counts[record[0]]++;
    summary.services = {
        scopes: counts[0],
        symbolTypesAtLocation: counts[1],
        symbolTypesWithoutLocation: counts[1],
        moduleExports: counts[2],
        aliasTargets: counts[3],
        localExportTargets: counts[4],
        shorthandValues: counts[5],
        parameterPropertyPairs: counts[6],
    };
}
if (declarationVisibility) {
    const phases = Array(5).fill(0);
    for (const result of results) for (const record of result.candidate?.visibilityQueries ?? []) phases[record[0]]++;
    summary.visibilityRecords = { initial: phases[0], afterPrecalculation: phases[1], aliasRetention: phases[2], afterRetention: phases[3], afterRepeatedPrecalculation: phases[4] };
}
if (symbolChains) {
    let found = 0, absent = 0, longest = 0;
    for (const result of results) {
        for (const record of result.candidate?.symbolChainQueries ?? []) {
            if (record[4] === null) absent++;
            else {
                found++;
                longest = Math.max(longest, record[4].length);
            }
        }
    }
    summary.chains = { found, absent, longest };
}
if (accessibility && !symbolDisplay) {
    const counts = Array(5).fill(0), states = Array(4).fill(0);
    for (const result of results) {
        for (const record of result.candidate?.accessibilityQueries ?? []) {
            counts[record[0]]++;
            if (record[0] === 1) states[record[6]]++;
        }
    }
    summary.accessibilityRecords = { containers: counts[0], decisions: counts[1], flagQueries: counts[2], typeAndValueQueries: counts[3], entityNames: counts[4], decisionStates: states };
    summary.symbolDiagnosticNamesCompared = false;
    summary.entityNameVisibilityComparedCompletely = true;
}
if (symbolDisplay && !symbolFormats) {
    let names = 0, accessibilityResults = 0;
    for (const entry of results) for (const row of entry.candidate?.symbolDisplayQueries ?? []) row[0] === 0 ? names++ : accessibilityResults++;
    summary.symbolDisplayRecords = { names, accessibilityResults };
}
if (symbolFormats) summary.symbolFormatFlags = computedSymbols ? [20, 22, 28, 30, 52, 54, 60, 62] : [0, 1, 2, 6, 8, 10, 12, 14, 16, 17, 32, 34, 36, 38, 40, 42, 44, 46, 64];
if (contextQueries) {
    const counts = Array(8).fill(0);
    for (const result of results) for (const record of result.candidate?.contextQueries ?? []) counts[record[0]]++;
    summary.contextRecords = {
        expressionTypes: counts[0],
        objectElements: counts[1],
        arrayPositions: counts[2],
        jsxAttributes: counts[3],
        resolvedSignaturesAndReturns: counts[4],
        argumentTypes: counts[5],
        signatureHelp: counts[6],
        stringCompletions: counts[7],
    };
}
await writeFile(path.join(output, "summary.json"), JSON.stringify(summary, null, 2));
if (process.argv.includes("--record")) await writeFile(path.join(root, "csharp/compatibility/evidence", option("--record") + ".json"), JSON.stringify(summary, null, 4) + "\n");
console.log(summary);
console.log(failures.map(f => ({ name: f.name, ...f.referenceError ? { referenceError: f.referenceError.slice(0, 180) } : {}, ...f.candidateError ? { candidateError: f.candidateError.slice(0, 180) } : {} })));
if (failures.length) process.exitCode = 1;
