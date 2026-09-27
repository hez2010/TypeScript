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

const directory = path.join(output, "checker-node-builder-tracking");
const internalOptions = process.argv.includes("--internal-flags");
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
interface Symbol{} interface SymbolConstructor{():symbol;readonly iterator:unique symbol} declare var Symbol:SymbolConstructor;type ReturnType<T extends (...args:any)=>any>=T extends (...args:any)=>infer R?R:any;
type NoInfer<T>=intrinsic;type Uppercase<T extends string>=intrinsic;`;
const fixtures = internalOptions ? {
    internalSignatures: "namespace N{export interface A{value:string}}const showGeneric=<T extends N.A>(value:T):N.A=>value;const showReturn=():N.A=>({value:'x'});",
    internalQualified: "namespace N{export interface A{value:string}export class C{value=1}}declare let showType:N.A;declare let showObject:{a:N.A;c:N.C};declare let showConstructor:typeof N.C;",
    internalUnresolved: "declare const key:any;declare const nested:{key:any};declare const numeric:number;declare let showAny:{[key]:string;[nested.key]:number;regular:boolean};declare let showRemoved:{[numeric]:string;[key()]:number};",
    internalComputed: "declare const key:unique symbol;declare let showKeys:{[key]:number;['text']:string;[42]:boolean};",
    internalCache: "namespace N{export interface A{x:number}}interface Box<T>{value:T}declare let showBox:Box<N.A>;declare let showNested:{a:Box<N.A>;b:Box<N.A>};",
} : {
    templates: "declare const value:string;let showLet=`v${value}`;const showConst=`v${value}`;const showArrow=()=>`v${value}` as const;",
    genericCache: "interface Box<T>{value:T}function scope<T>(){let showGeneric:Box<T>;let showConditional:T extends string?Box<T>:T[];}type Tree<T>={next:Tree<T>};declare let showTree:Tree<number>;",
    unicode: 'type Text="雪";declare let showWide:{["雪"]:Text; callback:()=>{雪:Text}};',
    inaccessible: 'namespace N{const key:unique symbol=Symbol();export let showUnique:typeof key;}class C{field!:{owner:this}}declare let showThis:C["field"];',
    computed: "declare const key:unique symbol;declare let showComputed:{[key]:number};namespace N{export const key:unique symbol=Symbol()}declare let showQualified:{[N.key]:string};",
    inferenceShapes: "declare let source:number;declare function get():number;const showSpread={source,...{x:1}};const showArray=[get()];const showTuple=[get()] as const;const showFn=(x=source)=>x;const showMethods={method(x:number){return get()},get value(){return source}};",
    names: "interface A{value:string}type Alias=A;declare let showAlias:Alias;declare let showObject:{x:A;y:Alias;f:<T extends A>(x:T)=>T};",
    tuples: "declare let showEmpty:[];declare let showReadonly:readonly [];declare let showTuple:[number,string?];",
    thisType: 'class C{value(){return {owner:this}}}declare let showReturn:ReturnType<C["value"]>;',
    unique: "function make(){const key:unique symbol=Symbol();return key}const showKey=make();const showObject={key:Symbol()};",
    classes: "const showClass=class{private p=1;protected q=2;#secret=3;value=4;get item(){return this}};",
    inferred: "declare function get():number;const showCall=get();const showObject={value:get()};const showFunction=()=>get();",
    imports: 'import {External as Imported,Item} from "./dependency";declare let showImport:Imported<Item>;',
};
const dependency = "export interface External<T>{item:T}export interface Item{value:string}";
const ignore = 2 ** 15 + 2 ** 16 + 2 ** 17 + 2 ** 18 + 2 ** 19 + 2 ** 21 + 2 ** 26;
const flags = internalOptions ? [ignore + 1] : [0, 1, ignore, ignore + 1, 1 + 2 ** 20, 1 + 2 ** 11, ignore + 1 + 2 ** 23];
const filterIndex = process.argv.indexOf("--filter");
const filter = filterIndex < 0 ? "" : process.argv[filterIndex + 1];
const cases = [];
for (const [fixture, text] of Object.entries(fixtures)) {
    for (const strict of [false, true]) {
        const name = `${fixture}-strict-${strict}-full-false`;
        if (filter && !new RegExp(filter).test(name)) continue;
        const input = {
            files: Object.fromEntries(
                Object.entries({ "/project/globals.d.ts": globals, "/project/main.ts": text, "/project/dependency.ts": dependency })
                    .map(([name, text]) => [name, Buffer.from(text).toString("base64")]),
            ),
            roots: ["/project/globals.d.ts", "/project/main.ts"],
            options: { noLib: true, strict, noErrorTruncation: false, target: "esnext" },
            concurrency: 1,
            typeNodes: true,
            nodeBuilderTracking: true,
            ...(internalOptions ? { nodeBuilderInternalFlags: [0, 1, 2, 4, 8, 15] } : {}),
            typeSyntaxFlags: flags,
        };
        cases.push({ name, input, hash: sha256(JSON.stringify(input)) });
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
    queries += reference.tracking.length;
    if (JSON.stringify(reference) === JSON.stringify(candidate)) continue;
    const mismatches = [];
    for (let i = 0; i < Math.max(reference.tracking.length, candidate.tracking.length); i++) if (JSON.stringify(reference.tracking[i]) !== JSON.stringify(candidate.tracking[i])) mismatches.push({ reference: reference.tracking[i], candidate: candidate.tracking[i] });
    if (mismatches.length || JSON.stringify(reference.diagnostics) !== JSON.stringify(candidate.diagnostics)) differences.push({ name: item.name, mismatches, referenceDiagnostics: reference.diagnostics, candidateDiagnostics: candidate.diagnostics });
}
await json(path.join(directory, "differences.json"), differences);
const summary = { timestamp: new Date().toISOString(), referenceRevision, managed: true, cases: cases.length, queries, differences: differences.map(d => ({ name: d.name, count: d.mismatches.length })), oracleSha256: oracleHash, candidateSha256: candidateHash, referenceExecuted: expected.executed, referenceReused: expected.reused, candidateExecuted: actual.executed, candidateReused: actual.reused };
await json(path.join(directory, "summary.json"), summary);
await json(path.join(directory, `summary-tracking${internalOptions ? "-internal-flags" : ""}${filter ? "-" + filter.replace(/[^a-z0-9-]/gi, "_") : ""}.json`), summary);
console.log(summary);
if (differences.length) process.exitCode = 1;
