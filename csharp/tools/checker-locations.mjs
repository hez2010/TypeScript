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
import { referenceRevision } from "./common.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const output = path.join(root, "built/csharp/checker-locations");
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
const inputs = [];
for (const [name, source] of Object.entries(fixtures)) {
    for (const strict of [false, true]) {
        for (const concurrency of [1, 4]) {
            const files = { "/project/globals.d.ts": library, [`/project/main.${name === "jsx" ? "tsx" : name === "jsdoc" ? "js" : "ts"}`]: source, "/project/dep.ts": "export interface Shape {value:number;} export const named=1; export default class Default { value=1; }" };
            inputs.push({ name: `${name}:${strict}:${concurrency}`, files: Object.fromEntries(Object.entries(files).map(([name, text]) => [name, Buffer.from(text).toString("base64")])), roots: Object.keys(files), options: { strict, target: "esnext", module: "esnext", moduleResolution: "bundler", jsx: "preserve", ...name === "jsdoc" ? { allowJs: true, checkJs: true } : {} }, typeNodes: true, locations: true, concurrency });
        }
    }
}
const selected = process.argv.includes("--filter") ? inputs.filter(input => input.name.includes(option("--filter"))) : inputs;
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
    if (candidate) queries += candidate.locationQueries.length;
    results.push({ input, reference, candidate, referenceError, candidateError });
    if (referenceError || candidateError) {
        failures.push({ name: input.name, referenceError, candidateError });
        continue;
    }
    comparedQueries += candidate.locationQueries.length;
    try {
        assert.deepStrictEqual(candidate, reference);
    }
    catch {
        failures.push({ name: input.name, input, reference, candidate });
    }
}
await writeFile(path.join(output, "failures.json"), JSON.stringify(failures, null, 2));
await writeFile(path.join(output, "results.json"), JSON.stringify(results));
const summary = { configurations: selected.length, queries, comparedQueries, exact: selected.length - failures.length, failed: failures.length, referenceRevision, referenceFailures, candidateFailures, oracleHash, candidateHash, inputHash: hash(JSON.stringify(selected)), resultHash: hash(JSON.stringify(results)), managed: true };
await writeFile(path.join(output, "summary.json"), JSON.stringify(summary, null, 2));
if (process.argv.includes("--record")) await writeFile(path.join(root, "csharp/compatibility/evidence", option("--record") + ".json"), JSON.stringify(summary, null, 4) + "\n");
console.log(summary);
console.log(failures.map(f => ({ name: f.name, ...f.referenceError ? { referenceError: f.referenceError.slice(0, 180) } : {}, ...f.candidateError ? { candidateError: f.candidateError.slice(0, 180) } : {} })));
if (failures.length) process.exitCode = 1;
