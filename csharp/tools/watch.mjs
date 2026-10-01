import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.join(output, "watch", option("--tag", "current"));
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const bridgeDirectory = path.join(source, "cmd/csharp-watch");
await mkdir(bridgeDirectory, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/watch/main.go"), path.join(bridgeDirectory, "main.go"));
const oracle = path.join(output, "watch/oracle.exe");
const managedDirectory = option("--managed-directory", path.join(output, "phase6-build/bin/TypeScript.Compatibility/release"));
const dll = path.join(managedDirectory, "TypeScript.Compatibility.dll");
if (!process.argv.includes("--no-build")) {
    await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-watch"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility/TypeScript.Compatibility.csproj", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase6-build"]);
}
const cwd = "/home/user/project/src";
const file = relative => `${cwd}/${relative}`;
const library = "/// <reference no-default-lib=\"true\"/>\ninterface Boolean {}\ninterface Function {}\ninterface CallableFunction {}\ninterface NewableFunction {}\ninterface IArguments {}\ninterface Number {}\ninterface Object {}\ninterface RegExp {}\ninterface String {}\ninterface Array<T> { length: number; [n: number]: T; }\ninterface ReadonlyArray<T> {}\n";
const args = (...options) => ["--watch", "--pretty", "false", "--singleThreaded", "--preserveWatchOutput", ...options];
const config = (options = {}, other = {}) => JSON.stringify({ compilerOptions: { lib: ["es5"], outDir: "dist", ...options }, ...other });
const files = options => ({ "tsconfig.json": config(options), "a.ts": "export function value(): number { return 1; }",
    "b.ts": "import { value } from './a'; export const result = value();" });
let cases = [];
function add(name, input, steps, command = args(), extra = {}) {
    const absolute = entries => Object.fromEntries(Object.entries(entries).map(([key, value]) => [key.startsWith("/") ? key : file(key), value]));
    cases.push({ name, cwd, files: { "/lib/lib.es5.d.ts": library, ...absolute(input) }, libraryDirectory: "/lib", ...extra,
        steps: [{ args: command }, {}, ...steps, {}].map(step => ({ ...step, ...(step.edits ? { edits: absolute(step.edits) } : {}), ...(step.touch ? { touch: step.touch.map(file) } : {}) })) });
}
for (const incremental of [false, true]) for (const declaration of [false, true]) for (const noEmit of [false, true]) {
    const options = { incremental, declaration, noEmit };
    add(`edits-${incremental}-${declaration}-${noEmit}`, files(options), [
        { edits: { "a.ts": "export function value(): number { return 2; }" } },
        { edits: { "a.ts": "export function value(): string { return 'text'; }" } },
        { edits: { "a.ts": "export const value: number = 'error';" } },
        { edits: { "a.ts": files(options)["a.ts"] } }, { touch: ["a.ts"] }, { overflow: true }
    ]);
}
for (const name of ["noEmit", "noCheck", "declaration", "sourceMap", "declarationMap", "emitDeclarationOnly", "skipLibCheck", "strict"]) {
    const original = { incremental: true, declaration: true, [name]: false };
    add(`options-${name}`, files(original), [
        { edits: { "tsconfig.json": config({ ...original, [name]: true }) } }, {},
        { edits: { "tsconfig.json": config(original) } }
    ]);
}
add("roots", files({ incremental: true }), [
    { edits: { "new.ts": "export const n = 10;" } }, { edits: { "new.ts": null } }, { edits: { "a.ts": null } },
    { edits: { "a.ts": files({})["a.ts"] } }, { edits: { "folder/nested.ts": "export const nested = true;" } }
]);
add("missing-module", { ...files({ incremental: true }), "a.ts": "export { value } from './missing';" }, [
    { edits: { "missing.ts": "export const value = 10;" } }, { edits: { "missing.ts": null } },
    { edits: { "missing/index.ts": "export const value = 10;" } }, { edits: { "missing/index.ts": null } }
]);
add("package-json", { ...files({ incremental: true, module: "nodenext" }), "package.json": "{\"type\":\"commonjs\"}" }, [
    { edits: { "package.json": "{\"type\":\"module\"}" } }, { edits: { "package.json": null } }
]);
add("external-package", { ...files({ incremental: true }), "a.ts": "export { value } from 'pkg';" }, [
    { edits: { "node_modules/pkg/package.json": "{\"types\":\"main.d.ts\"}", "node_modules/pkg/main.d.ts": "export declare const value: number;" } },
    { edits: { "node_modules/pkg/package.json": "{\"types\":\"other.d.ts\"}", "node_modules/pkg/other.d.ts": "export declare const value: string;" } },
    { edits: { "node_modules/pkg/other.d.ts": null } }
]);
add("config-extends", { ...files({ incremental: true }), "base.json": "{\"compilerOptions\":{\"declaration\":true}}",
    "tsconfig.json": "{\"extends\":\"./base.json\",\"compilerOptions\":{\"incremental\":true,\"outDir\":\"dist\",\"lib\":[\"es5\"]}}" }, [
    { edits: { "base.json": "{\"compilerOptions\":{\"declaration\":true,\"sourceMap\":true}}" } }, { edits: { "base.json": null } },
    { edits: { "base.json": "{}" } }
]);
add("config-delete-restore", files({ incremental: true }), [
    { edits: { "tsconfig.json": null } }, {}, { edits: { "tsconfig.json": config({ incremental: true }) } }
]);
add("config-syntax-error", files({ incremental: true }), [
    { edits: { "tsconfig.json": "{\"compilerOptions\":{\"lib\":[\"es5\"],\"incremental\":true,\"outDir\":\"dist\"}" } },
    { edits: { "tsconfig.json": config({ incremental: true }) } }
]);
add("irrelevant-files", files({ incremental: true }), [
    { edits: { "notes.txt": "notes" } }, { edits: { ".git/index": "index" } }, { edits: { "node_modules/.cache/file.ts": "let bad: number = '';" } },
    { edits: { "dist/a.js": "// modified output" } }
]);
add("config-includes", { ...files({ incremental: true }), "tsconfig.json": config({ incremental: true }, { include: ["*.ts"], exclude: ["ignored.ts"] }) }, [
    { edits: { "ignored.ts": "const value: number = '';" } }, { edits: { "nested/ignored.ts": "const value: number = '';" } },
    { edits: { "new.ts": "export const added = true;" } }
]);
add("files-list", { ...files({ incremental: true }), "tsconfig.json": config({ incremental: true }, { files: ["b.ts"] }) }, [
    { edits: { "unrelated.ts": "const value: number = '';" } }, { edits: { "a.ts": "export const value: number = 10;" } }
]);
add("case-insensitive", files({ incremental: true }), [{ edits: { "A.ts": "export function value(): string { return 'text'; }" } }], args(), { caseInsensitive: true });
add("clear-screen", files({}), [{ edits: { "a.ts": "export const value = () => 2;" } }], ["--watch", "--pretty", "false", "--singleThreaded"]);
const buildArgs = (...options) => ["--build", "--watch", "--verbose", "--pretty", "false", "--singleThreaded", "--preserveWatchOutput", ...options];
for (const composite of [false, true]) {
    add(`build-single-${composite}`, files({ composite, incremental: true, declaration: true }), [
        { edits: { "a.ts": "export function value(): number { return 2; }" } },
        { edits: { "a.ts": "export function value(): string { return 'text'; }" } },
        { edits: { "new.ts": "export const added = true;" } }, { edits: { "new.ts": null } },
        { touch: ["a.ts"] }, { overflow: true }, { edits: { "notes.txt": "notes" } }
    ], buildArgs());
}
const solution = { "tsconfig.json": '{"files":[],"references":[{"path":"./a"},{"path":"./b"}]}',
    "a/tsconfig.json": config({ composite: true }), "a/a.ts": "export function value(): number { return 1; }",
    "b/tsconfig.json": config({ composite: true }, { references: [{ path: "../a" }] }),
    "b/b.ts": "import { value } from '../a/a'; export const result = value();" };
for (const stop of [false, true]) add(`build-solution-${stop}`, solution, [
    { edits: { "a/a.ts": "export function value(): number { return 2; }" } },
    { edits: { "a/a.ts": "export function value(): string { return 'text'; }" } },
    { edits: { "a/a.ts": "export const value: number = 'error';" } },
    { edits: { "a/a.ts": solution["a/a.ts"] } },
    { edits: { "a/tsconfig.json": config({ composite: true, sourceMap: true }) } },
    { edits: { "a/tsconfig.json": null } }, { edits: { "a/tsconfig.json": solution["a/tsconfig.json"] } },
    { edits: { "tsconfig.json": '{"files":[],"references":[{"path":"./a"}]}' } },
    { edits: { "tsconfig.json": solution["tsconfig.json"] } }, { overflow: true }
], buildArgs(...(stop ? ["--stopBuildOnErrors"] : [])));
add("build-packages", { ...files({ incremental: true }), "a.ts": "export { value } from 'pkg';" }, [
    { edits: { "node_modules/pkg/package.json": '{"types":"main.d.ts"}', "node_modules/pkg/main.d.ts": "export declare const value: number;" } },
    { edits: { "node_modules/pkg/main.d.ts": "export declare const value: string;" } },
    { edits: { "node_modules/pkg/package.json": '{"types":"other.d.ts"}', "node_modules/pkg/other.d.ts": "export declare const value: string;" } },
    { edits: { "node_modules/pkg/other.d.ts": null } }
], buildArgs());
const filter = option("--filter", ""); if (filter) cases = cases.filter(test => new RegExp(filter).test(test.name));
const limit = option("--limit", ""); if (limit) cases = cases.slice(0, Number(limit));
await json(path.join(directory, "inputs.json"), cases);
async function execute(command, args) {
    const child = spawn(command, args, { cwd: root, windowsHide: true });
    let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {}); child.stdin.end(cases.map(test => JSON.stringify(test)).join("\n") + "\n");
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    if (code) throw Error(`${command} exited ${code}: ${stderr}`);
    const rows = stdout.trim().split(/\r?\n/).map(JSON.parse); assert.equal(rows.length, cases.length);
    return rows;
}
const binaries = { reference: oracle, candidate: dll, compiler: path.join(managedDirectory, "TypeScript.Compiler.dll") };
const hashes = Object.fromEntries(await Promise.all(Object.entries(binaries).map(async ([key, file]) => [key, sha256(await readFile(file))])));
const before = await execute(oracle, []), after = await execute(dotnet, [dll, "--watch-lines"]);
await json(path.join(directory, "reference.json"), before); await json(path.join(directory, "candidate.json"), after);
const normalize = rows => Array.isArray(rows) ? rows.map(row => ({ ...row, stdout: row.stdout.replace(/(^|\x1b\[H)\d{2}:\d{2}:\d{2} [AP]M - /gm, "$1<time> - ") })) : rows;
const failures = [];
for (let index = 0; index < cases.length; index++) {
    try { assert.deepEqual(normalize(after[index]), normalize(before[index])); }
    catch { failures.push({ index, name: cases[index].name, inputHash: sha256(JSON.stringify(cases[index])), reference: normalize(before[index]), candidate: normalize(after[index]) }); }
}
for (const [name, file] of Object.entries(binaries)) assert.equal(sha256(await readFile(file)), hashes[name], `${name} changed during validation`);
const summary = { referenceRevision, cases: cases.length, cycles: cases.reduce((count, test) => count + test.steps.length, 0), strictMatches: cases.length - failures.length,
    failures: failures.length, normalizedFields: ["stdout: status-report clock prefix"], hashes, inputHash: sha256(JSON.stringify(cases)),
    command: "node csharp/tools/watch.mjs --dotnet <dotnet.exe> --record" };
await json(path.join(directory, "failures.json"), failures); await json(path.join(directory, "summary.json"), summary);
if (process.argv.includes("--record")) await json(path.join(root, "csharp/compatibility/evidence/phase6-watch.json"), summary);
console.log(JSON.stringify(summary, null, 2));
if (failures.length) console.log(failures.map(failure => failure.name).join("\n"));
assert.equal(failures.length, 0);
