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

const directory = path.join(output, "checker-display-formats");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const go = "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe";
const dotnet = "D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe";
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const oracle = path.join(directory, "oracle.exe");
await mkdir(directory, { recursive: true });
const sources = ["main.go", "bridge.go"];
const sourceHash = sha256(Buffer.concat(await Promise.all(sources.map(name => readFile(path.join(root, "csharp/oracle/checker-program", name))))));
let cached;
try {
    cached = JSON.parse(await readFile(path.join(directory, "oracle-build.json"), "utf8"));
    if (cached.executableSha256 !== sha256(await readFile(oracle))) cached = null;
}
catch (error) {
    if (error.code !== "ENOENT") throw error;
    cached = null;
}
if (cached?.sourceSha256 !== sourceHash || cached?.referenceRevision !== referenceRevision) {
    await copyFile(path.join(root, "csharp/oracle/checker-program/main.go"), path.join(source, "cmd/checker-program-probe/main.go"));
    await copyFile(path.join(root, "csharp/oracle/checker-program/bridge.go"), path.join(source, "internal/checker/csharp_program_probe.go"));
    await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/checker-program-probe"], {
        env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" },
    });
    await json(path.join(directory, "oracle-build.json"), { referenceRevision, sourceSha256: sourceHash, executableSha256: sha256(await readFile(oracle)) });
}
const globals = `interface Object{} interface Function{} interface CallableFunction{} interface NewableFunction{} interface IArguments{}
interface String{} interface Number{} interface Boolean{} interface RegExp{}
interface Array<T>{length:number;[n:number]:T} interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}
interface Symbol{} interface SymbolConstructor{readonly iterator:unique symbol} declare var Symbol:SymbolConstructor;
type NoInfer<T>=intrinsic;type Uppercase<T extends string>=intrinsic;`;
const fixtures = {
    objects: `type Text="雪"|"it's";declare let showObject:{a:number;b?:{nested:string};readonly "雪":Text;cb:(x:string)=>{nested:boolean}};
declare let showArray:readonly(number|string)[];declare let showTuple:[number,{item:string},...string[]];declare let showEmpty:{};`,
    signatures: `declare let showFunction:<T extends {x:string},U=T>(this:{id:number},x:T,y?:U,...args:T[])=>T;
declare let showConstructor:abstract new<T extends string>(arg:T)=>{item:T};
declare let showOverload:{(x:string):number;(x:number):string;new(x:number):{p:number};p?:boolean};
declare let showPredicate:(値:unknown)=>値 is string;declare let showAsserts:(x:unknown)=>asserts x;`,
    classes: `class Base{private hidden!:string;protected p=0;constructor(public value:number){}}
const anonymous=class{readonly p=1;m(value:string){return value}};
declare let showClass:typeof Base;declare let showAnonymous:typeof anonymous;declare let showInstance:Base;`,
    scopes: `namespace N{export interface Box<T>{value:T}export type Alias<T>=[T,{nested:string}];export declare let showInner:Box<Alias<number>>}
declare let showOuter:N.Box<N.Alias<string>>;
function outer<T extends {id:number}>(input:T){type Local={p:T};let showLocal:Local;let showShadow:<T>(x:T)=><T>(y:T)=>[typeof input,T];}
declare let showModule:typeof N;`,
    operators: [
        "type M<T>={readonly[P in keyof T as `get${Uppercase<P & string>}`]?:T[P]};",
        "type Conditional<T>=T extends {x:infer U extends string}?U:never;",
        "declare let showMapped:<T>()=>M<T>;declare let showConditional:<T>()=>Conditional<T>;",
        "declare let showIndex:<T,K extends keyof T>()=>T[K];declare let showKeys:<T>()=>keyof T;",
        "declare const unique:unique symbol;declare let showUnique:typeof unique;",
    ].join("\n"),
    enums: `enum E{A,B}declare let showEnum:E;declare let showMember:E.A;
declare let showLiterals:"雪"|"line\\nend"|"it's"|123n|-12;
declare let showTemplate:<T extends string>()=>` + "`雪${T}`;" + `
declare let showIntersection:{kind:"a";a:number}&{kind:"b";b:string};`,
    long: `type Long={${Array.from({ length: 70 }, (_, i) => `property${i}:{nested:"value${i}";other:number}`).join(";")}};
declare let showLong:Long;declare let showLongFunction:(arg:Long)=>Long;`,
    imports: `import {External as Imported,Item} from "./dependency";declare let showImport:Imported<Item>;
namespace N{interface Imported<T>{local:T}export declare let showQualified:import("./dependency").External<{x:string}>;}`,
    constraints: `type Alias={q:string};declare let showConstrained:<T extends Alias>(arg:Alias)=>Alias;
declare function initialized(first:{p:number}|undefined,required:string):{result:string};declare let showInitialized:typeof initialized;
declare let showRest:(...args:[name:string,name:number])=>void;`,
    initializers: `function initialized(first:{p:number}={p:0},required:string){return {result:required}}declare let showInitialized:typeof initialized;
declare function make<T>(value:T):T;declare let showInstantiated:typeof make<string>;`,
};
const dependency = "export interface External<T>{item:T}export interface Item{value:string}";
const combinations = process.argv.includes("--combinations");
const flags = combinations ? [3, 1 + 2 ** 10 + 2 ** 11, 2 ** 10 + 2 ** 18 + 2 ** 27, 2 ** 2 + 2 ** 6 + 2 ** 23, 2 ** 10 + 2 ** 14 + 2 ** 28, 2 ** 25 + 2 ** 18, 2 ** 8 + 2 ** 18, 2 ** 5 + 2 ** 18]
    : [0, ...[0, 1, 2, 3, 5, 6, 8, 10, 11, 12, 13, 14, 17, 18, 19, 20, 21, 22, 23, 25, 27, 28, 29, 30, 31].map(bit => 2 ** bit), 2 ** 10 + 2 ** 23, 2 ** 10 + 2 ** 23 + 1, 2 ** 18 + 2 ** 27, 2 ** 28 + 2 ** 23, 2 ** 2 + 2 ** 6, 2 ** 14 + 2 ** 20];
const filterIndex = process.argv.indexOf("--filter");
const filter = filterIndex < 0 ? "" : process.argv[filterIndex + 1];
const cases = [];
for (const [fixture, text] of Object.entries(fixtures)) {
    for (const strict of [false, true]) {
        for (const noErrorTruncation of [false, true]) {
            const name = `${fixture}-strict-${strict}-full-${noErrorTruncation}${combinations ? "-combined" : ""}`;
            if (!name.includes(filter)) continue;
            const input = {
                files: Object.fromEntries(
                    Object.entries({ "/project/globals.d.ts": globals, "/project/main.ts": text, "/project/dependency.ts": dependency })
                        .map(([name, text]) => [name, Buffer.from(text).toString("base64")]),
                ),
                roots: ["/project/globals.d.ts", "/project/main.ts"],
                options: { noLib: true, strict, noErrorTruncation, target: "esnext" },
                concurrency: 1,
                typeNodes: true,
                displayFormats: true,
                typeFormatFlags: flags,
            };
            cases.push({ name, input, hash: sha256(JSON.stringify(input)) });
        }
    }
}
await json(path.join(directory, "inputs.json"), cases);
if (process.argv.includes("--list")) {
    console.log(cases.map(item => ({ name: item.name, sha256: item.hash })));
    process.exit(0);
}
const oracleHash = sha256(await readFile(oracle));
const candidateHash = sha256(Buffer.concat(await Promise.all([dll, path.join(path.dirname(dll), "TypeScript.Compiler.dll")].map(p => readFile(p)))));
async function evaluate(kind, executable, args, executableHash) {
    const outputs = new Map(), missing = [];
    for (const item of cases) {
        const cache = path.join(directory, `${kind}-${item.hash}-${executableHash}.json`);
        try {
            outputs.set(item.name, JSON.parse(await readFile(cache, "utf8")));
        }
        catch (error) {
            if (error.code !== "ENOENT") throw error;
            missing.push({ ...item, cache });
        }
    }
    if (missing.length) {
        const stdout = [], stderr = [];
        const child = spawn(executable, args, { cwd: root, windowsHide: true });
        child.stdout.on("data", chunk => stdout.push(chunk));
        child.stderr.on("data", chunk => stderr.push(chunk));
        child.stdin.end(missing.map(item => JSON.stringify(item.input)).join("\n") + "\n");
        const code = await new Promise((resolve, reject) => {
            child.on("error", reject);
            child.on("close", resolve);
        });
        if (code !== 0) throw Error(`${kind} exited ${code}: ${Buffer.concat(stderr)}`);
        const rows = Buffer.concat(stdout).toString("utf8").trim().split(/\r?\n/).map(JSON.parse);
        if (rows.length !== missing.length) throw Error(`${kind} returned ${rows.length}/${missing.length} cases`);
        for (let i = 0; i < missing.length; i++) {
            outputs.set(missing[i].name, rows[i]);
            await json(missing[i].cache, rows[i]);
        }
    }
    return { outputs, reused: cases.length - missing.length, executed: missing.length };
}
const [expected, actual] = await Promise.all([
    evaluate("reference", oracle, [], oracleHash),
    evaluate("candidate", dotnet, [dll, "--checker-program-lines"], candidateHash),
]);
const differences = [];
let queries = 0;
for (const item of cases) {
    const reference = expected.outputs.get(item.name), candidate = actual.outputs.get(item.name);
    queries += reference.formats.length;
    if (JSON.stringify(reference) === JSON.stringify(candidate)) continue;
    const mismatches = [];
    for (let i = 0; i < Math.max(reference.formats.length, candidate.formats.length); i++) if (JSON.stringify(reference.formats[i]) !== JSON.stringify(candidate.formats[i])) mismatches.push({ reference: reference.formats[i], candidate: candidate.formats[i] });
    if (mismatches.length || JSON.stringify(reference.diagnostics) !== JSON.stringify(candidate.diagnostics)) differences.push({ name: item.name, mismatches, referenceDiagnostics: reference.diagnostics, candidateDiagnostics: candidate.diagnostics });
}
await json(path.join(directory, "differences.json"), differences);
const summary = { timestamp: new Date().toISOString(), referenceRevision, managed: true, cases: cases.length, queries, differences: differences.map(d => ({ name: d.name, count: d.mismatches.length })), oracleSha256: oracleHash, candidateSha256: candidateHash, referenceExecuted: expected.executed, referenceReused: expected.reused, candidateExecuted: actual.executed, candidateReused: actual.reused };
await json(path.join(directory, "summary.json"), summary);
await json(path.join(directory, `summary-${combinations ? "combinations" : "basic"}${filter ? "-" + filter : ""}.json`), summary);
console.log(summary);
if (differences.length) process.exitCode = 1;
