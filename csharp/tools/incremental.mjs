import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.join(output, "incremental", option("--tag", "current"));
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const bridgeDirectory = path.join(source, "cmd/csharp-incremental");
await mkdir(bridgeDirectory, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/incremental/main.go"), path.join(bridgeDirectory, "main.go"));
const oracle = path.join(output, "incremental/oracle.exe");
const managedDirectory = option("--managed-directory", path.join(output, "phase6-build/bin/TypeScript.Compatibility/release"));
const dll = path.join(managedDirectory, "TypeScript.Compatibility.dll");
if (!process.argv.includes("--no-build")) {
    await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-incremental"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility/TypeScript.Compatibility.csproj", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase6-build"]);
}
let cases = [];
const baseOptions = { incremental: true, lib: ["es5"], module: "esnext", moduleResolution: "bundler", outDir: "/out" };
const config = (options = {}) => JSON.stringify({ compilerOptions: { ...baseOptions, ...options }, include: ["*.ts", "*.json"] });
const sources = {
    "/source/a.ts": "export function value(): number { return 1; }",
    "/source/b.ts": "import { value } from './a'; export const result = value();",
    "/source/c.ts": "import { result } from './b'; export const copy = result;",
    "/source/d.ts": "import { copy } from './c'; const check: number = copy;",
    "/source/unrelated.ts": "export const unrelated = true;"
};
function add(name, options, steps, files = sources) {
    cases.push({ name, files: { ...files, "/source/tsconfig.json": config(options) }, steps: [{}, {}, ...steps, {}, { fresh: true }], singleThreaded: true });
}
for (const declaration of [false, true]) for (const composite of [false, true])
    for (const isolatedModules of [false, true]) for (const assumeChangesOnlyAffectDirectDependencies of [false, true]) {
        const options = { declaration, composite, isolatedModules, assumeChangesOnlyAffectDirectDependencies };
        if (composite && !declaration) continue;
        const name = `${declaration}-${composite}-${isolatedModules}-${assumeChangesOnlyAffectDirectDependencies}`;
        add(`edits-${name}`, options, [
            { edits: { "/source/a.ts": "export function value(): number { return 2; }" } },
            { edits: { "/source/a.ts": "export function value(): string { return 'two'; }" } },
            { edits: { "/source/a.ts": sources["/source/a.ts"] } }
        ]);
        add(`delete-recreate-${name}`, options, [
            { edits: { "/source/a.ts": null } }, { edits: { "/source/a.ts": sources["/source/a.ts"] } }
        ]);
    }
for (const declaration of [false, true]) for (const composite of [false, true]) {
    if (composite && !declaration) continue;
    for (const flag of ["noEmit", "noEmitOnError", "noCheck", "declarationMap", "sourceMap", "inlineSourceMap", "removeComments", "skipLibCheck", "strict", "isolatedDeclarations"])
        for (const initial of [false, true]) {
            const options = { declaration, composite, [flag]: initial };
            add(`option-${flag}-${initial}-${declaration}-${composite}`, options, [
                { edits: { "/source/tsconfig.json": config({ ...options, [flag]: !initial }) } },
                { fresh: true }, { edits: { "/source/a.ts": "export function value(): string { return 'two'; }" } },
                { edits: { "/source/tsconfig.json": config(options), "/source/a.ts": sources["/source/a.ts"] } }
            ]);
        }
}
for (const options of [{}, { declaration: true }, { declaration: true, composite: true }]) {
    for (const [name, original, changed] of [
        ["global", "interface Global { value: number; }", "interface Global { value: string; }"],
        ["ambient", "declare module 'ambient' { export const value: number; }", "declare module 'ambient' { export const value: string; }"],
        ["augmentation", "export {}; declare global { interface Global { value: number; } }", "export {}; declare global { interface Global { value: string; } }"],
        ["const-enum", "export const enum Values { A = 1 }", "export const enum Values { A = 2 }"],
        ["syntax", "export const value = ;", "export const value = 1;"],
        ["semantic", "export const value: number = 'error';", "export const value: number = 1;"],
    ]) add(`${name}-${JSON.stringify(options)}`, options, [{ edits: { "/source/a.ts": changed } }], {
        "/source/a.ts": original,
        "/source/use.ts": name === "const-enum" ? "import { Values } from './a'; export const a = Values.A;"
            : name === "global" || name === "augmentation" ? "export const a: Global = { value: 1 };"
            : name === "ambient" ? "import { value } from 'ambient'; export const a = value;" : "import { value } from './a'; export const a = value;"
    });
}
add("json-changes", { resolveJsonModule: true, declaration: true }, [
    { edits: { "/source/data.json": "{\"value\":\"text\"}" } }, { edits: { "/source/data.json": "{\"value\":2}" } }
], { "/source/data.json": "{\"value\":1}", "/source/a.ts": "import data from './data.json'; export const value = data.value;" });
for (const extension of ["mts", "cts"]) add(`node-format-${extension}`, { module: "nodenext", moduleResolution: "nodenext", declaration: true }, [
    { edits: { "/source/package.json": "{\"type\":\"module\"}" } },
    { edits: { "/source/a.ts": "export const a = 2;" } }
], { "/source/a.ts": "export const a = 1;", [`/source/entry.${extension}`]: "export const entry = true;" });
for (const bad of ["{", "{}", "{\"version\":\"old\",\"fileNames\":[\"./a.ts\"]}"])
    add(`corrupt-state-${bad}`, { declaration: true }, [{ fresh: true, edits: { "/out/tsconfig.tsbuildinfo": bad } }]);
for (const file of ["/out/a.js", "/out/a.d.ts", "/out/tsconfig.tsbuildinfo"])
    add(`write-failure-${file}`, { declaration: true }, [{ edits: { "/source/a.ts": "export const changed = true;" }, failWrite: file }]);
for (const [name, initial, changed] of [
    ["target", "es2015", "esnext"], ["module", "esnext", "preserve"],
    ["outDir", "/out", "/other"], ["rootDir", "/source", "/"],
    ["declarationDir", "/declarations", "/types"], ["tsBuildInfoFile", "/out/a.tsbuildinfo", "/out/b.tsbuildinfo"],
    ["newLine", "lf", "crlf"], ["emitBOM", false, true], ["emitDeclarationOnly", false, true]
]) for (const composite of [false, true]) {
    const options = { declaration: true, composite, [name]: initial };
    add(`path-option-${name}-${composite}`, options, [
        { edits: { "/source/tsconfig.json": config({ ...options, [name]: changed }) } },
        { fresh: true }, { edits: { "/source/tsconfig.json": config(options) } }
    ]);
}
for (const noEmit of [false, true]) for (const code of [
    "export const exports = 1;", "export const __esModule = true;", "export const WeakMap = 1; export class C { #value = 1; }"
]) {
    const options = { module: "commonjs", moduleResolution: "node16", target: "es2015", noEmit };
    // Node16 resolution requires matching module selection, so use a valid Node16 CommonJS file.
    options.module = "node16";
    add(`emit-diagnostic-${noEmit}-${code}`, options, [
        { edits: { "/source/tsconfig.json": config({ ...options, noEmit: !noEmit }) } },
        { fresh: true }, { edits: { "/source/tsconfig.json": config(options) } }
    ], { "/source/a.ts": code });
}
const filter = option("--filter", "");
if (filter) cases = cases.filter(test => new RegExp(filter).test(test.name));
const limit = option("--limit", ""); if (limit) cases = cases.slice(0, Number(limit));
await json(path.join(directory, "inputs.json"), cases);
async function execute(command, args, requests = cases) {
    const child = spawn(command, args, { cwd: root, windowsHide: true });
    let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {}); child.stdin.end(requests.map(test => JSON.stringify(test)).join("\n") + "\n");
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    if (code) throw Error(`${command} exited ${code}: ${stderr}`);
    const rows = stdout.trim().split(/\r?\n/).map(JSON.parse); assert.equal(rows.length, requests.length);
    return rows;
}
const hashes = { reference: sha256(await readFile(oracle)), candidate: sha256(await readFile(dll)), compiler: sha256(await readFile(path.join(managedDirectory, "TypeScript.Compiler.dll"))) };
const before = await execute(oracle, []), after = await execute(dotnet, [dll, "--incremental-lines"]);
await json(path.join(directory, "reference.json"), before); await json(path.join(directory, "candidate.json"), after);
const failures = [];
for (let index = 0; index < cases.length; index++) {
    try { assert.deepEqual(after[index], before[index]); }
    catch { failures.push({ index, name: cases[index].name, inputHash: sha256(JSON.stringify(cases[index])), reference: before[index], candidate: after[index] }); }
}
let interopCases = 0;
if (process.argv.includes("--interop")) {
    const handoffs = [];
    for (const [producer, states] of [["Go", before], ["CSharp", after]]) for (let index = 0; index < cases.length; index++) {
        if (!Array.isArray(states[index])) continue;
        const test = cases[index], files = { ...test.files };
        for (let cycle = 0; cycle < test.steps.length - 1; cycle++) {
            for (const [file, text] of Object.entries(test.steps[cycle].edits ?? {})) {
                if (text === null) delete files[file]; else files[file] = text;
            }
            for (const write of states[index][cycle].writes ?? [])
                files[write.path] = write.buildInfo ? JSON.stringify(write.buildInfo) : Buffer.from(write.textBase64, "base64").toString("utf8");
            if (![0, 2, 4].includes(cycle)) continue;
            handoffs.push({ ...test, name: `${producer}-handoff-${test.name}-${cycle}`, files: { ...files },
                steps: [{ fresh: true }, ...test.steps.slice(cycle + 1)] });
        }
    }
    await json(path.join(directory, "handoff-inputs.json"), handoffs);
    const expected = await execute(oracle, [], handoffs), actual = await execute(dotnet, [dll, "--incremental-lines"], handoffs);
    await json(path.join(directory, "handoff-reference.json"), expected); await json(path.join(directory, "handoff-candidate.json"), actual);
    interopCases = handoffs.length;
    for (let index = 0; index < handoffs.length; index++) {
        try { assert.deepEqual(actual[index], expected[index]); }
        catch { failures.push({ index, name: handoffs[index].name, inputHash: sha256(JSON.stringify(handoffs[index])), reference: expected[index], candidate: actual[index] }); }
    }
}
for (const [name, file] of Object.entries({ reference: oracle, candidate: dll, compiler: path.join(managedDirectory, "TypeScript.Compiler.dll") }))
    assert.equal(sha256(await readFile(file)), hashes[name], `${name} changed during validation`);
const summary = { referenceRevision, cases: cases.length, cycles: cases.reduce((count, test) => count + test.steps.length, 0),
    interopCases, strictMatches: cases.length + interopCases - failures.length, failures: failures.length, hashes, inputHash: sha256(JSON.stringify(cases)),
    command: "node csharp/tools/incremental.mjs --dotnet <dotnet.exe> --interop --record" };
await json(path.join(directory, "failures.json"), failures); await json(path.join(directory, "summary.json"), summary);
if (process.argv.includes("--record")) await json(path.join(root, "csharp/compatibility/evidence/phase6-incremental.json"), summary);
console.log(JSON.stringify(summary, null, 2));
if (failures.length) console.log(failures.slice(0, 12).map(failure => failure.name).join("\n"));
assert.equal(failures.length, 0);
